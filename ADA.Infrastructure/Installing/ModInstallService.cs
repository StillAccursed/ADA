using System.Collections.Concurrent;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Settings;
using ADA.Infrastructure.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Installing;

/// <param name="Done">Bytes downloaded, or files extracted.</param>
public sealed record ModInstallProgress(string Reference, InstallStage Stage, long Done, long? Total);

/// <param name="RecoveryDeferred">An interrupted transaction was found, but the game is running; it is finished next time.</param>
public sealed record GameModsStatus(RecoveryResult Recovery, bool RecoveryDeferred, CheckResult Check);

/// <param name="Failed">Mod → why it could not be repaired.</param>
public sealed record RepairResult(IReadOnlyList<string> Repaired, IReadOnlyDictionary<string, string> Failed);

/// <summary>
/// The app's entry point for changing a game's mods: downloads verified archives (with the user's
/// retry setting), hands them to <see cref="ModInstaller"/>, then applies the "Keep downloaded
/// archives" setting. Also runs launch-time recovery and checks, and repairs damaged mods.
/// </summary>
/// <remarks>
/// Every method does its disk and network work off the calling thread, so it is safe to call from the UI.
/// <c>isGameRunning</c> answers for one install; local installs answer for every Satisfactory
/// process on this machine, since processes can't be matched to an install.
/// </remarks>
public sealed class ModInstallService(
    SettingsStore settings,
    ArchiveDownloader downloader,
    IModVersionSource versions,
    Func<GameInstall, CancellationToken, Task<bool>> isGameRunning,
    ILogger<ModInstallService>? log = null,
    GameFilesFactory? files = null)
{
    // Enough to fill a typical connection without hammering ficsit.app.
    private const int PARALLEL_DOWNLOADS = 3;
    private const int PARALLEL_LOOKUPS = 4;

    private readonly ILogger _log = log ?? NullLogger<ModInstallService>.Instance;
    private readonly GameFilesFactory _files = files ?? new GameFilesFactory();
    private readonly ConcurrentDictionary<string, byte> _operations = new(StringComparer.Ordinal);

    /// <summary>Raised on the operation's calling thread when a mutation finishes or fails.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether this service is currently checking or changing the captured installation.</summary>
    internal bool IsChanging(GameInstall game) => _operations.ContainsKey(game.GamePath);

    /// <summary>The build a game needs: clients always use Windows builds (also under CrossOver and Proton).</summary>
    public static TargetPlatform TargetFor(GameInstall game) => game.Kind switch
    {
        GameKind.DedicatedServer when game.Host == GameHost.Linux => TargetPlatform.LinuxServer,
        GameKind.DedicatedServer => TargetPlatform.WindowsServer,
        _ => TargetPlatform.Windows,
    };

    /// <summary>
    /// A new installer for the game, on its local or server files. It re-checks that this game
    /// isn't running just before it changes anything, blocking the calling thread, so only call it
    /// inside <c>Task.Run</c>: there the wait blocks a pool thread, never the UI.
    /// </summary>
    /// <remarks>
    /// The re-check throws <see cref="GameRunningException.For"/> itself, because the installer
    /// doesn't know whether it's working on a server and would otherwise say to close the game.
    /// </remarks>
    /// <exception cref="Servers.ServerUnavailableException">The game is a server that isn't connected.</exception>
    public ModInstaller InstallerFor(GameInstall game) =>
        new(_files.For(game), () => isGameRunning(game, CancellationToken.None).GetAwaiter().GetResult()
            ? throw GameRunningException.For(game)
            : false, checkpoint: null, log: _log);

    /// <summary>
    /// Run on launch for each detected game: finish or undo an interrupted transaction, then the
    /// fast check. Recovery waits if the game is running, because it would move folders the game has open.
    /// </summary>
    public Task<GameModsStatus> RecoverAndCheckAsync(GameInstall game, CancellationToken ct = default) => Task.Run(async () =>
    {
        using var operation = BeginOperation(game);
        var installer = InstallerFor(game);
        var deferred = installer.HasInterruptedTransaction && await isGameRunning(game, ct);
        var recovery = deferred ? RecoveryResult.CLEAN : installer.Recover();
        if (recovery.HadInterruptedTransaction)
        {
            _log.LogWarning("Recovered an interrupted transaction in {Game}: completed {Forward}, discarded {Discarded}, rolled back {Back}",
                game.GamePath, recovery.RolledForward, recovery.Discarded, recovery.RolledBack);
        }
        return new GameModsStatus(recovery, deferred, installer.Check());
    }, ct);

    /// <summary>
    /// Downloads (or reuses kept archives for) the given versions, then installs them in the given
    /// order in one transaction. Callers put dependencies first.
    /// </summary>
    /// <exception cref="GameRunningException">Checked before anything is downloaded.</exception>
    /// <exception cref="ArchiveDownloadException">A download failed every attempt; nothing was installed.</exception>
    /// <exception cref="InstallException">The install failed and was rolled back.</exception>
    public Task<TransactionResult> InstallAsync(GameInstall game, IReadOnlyList<CatalogVersion> versions,
        IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default) =>
        ApplyAsync(game, versions, [], progress, ct);

    /// <summary>
    /// Uninstalls <paramref name="uninstall"/> and installs <paramref name="install"/> (dependencies
    /// first) in one transaction, so a failure leaves the game exactly as it was.
    /// </summary>
    /// <inheritdoc cref="InstallAsync" path="/exception"/>
    public async Task<TransactionResult> ApplyAsync(GameInstall game, IReadOnlyList<CatalogVersion> install,
        IReadOnlyList<string> uninstall, IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        using var operation = BeginOperation(game);
        return await ApplyReservedAsync(game, install, uninstall, progress, ct);
    }

    /// <summary>Reserves the game through planning, file commit and durable profile activation.</summary>
    internal async Task ApplyProfileAsync(GameInstall game, Func<Task<ModChangePlan>> planProfile,
        string name, Action<string> activate, IProgress<ModInstallProgress>? progress, CancellationToken ct)
    {
        using var operation = BeginOperation(game);
        if (await isGameRunning(game, ct))
        {
            throw GameRunningException.For(game);
        }
        var installer = InstallerFor(game);
        installer.ThrowIfUnsettled("switching profiles");
        var plan = await planProfile();
        if (!plan.Succeeded)
        {
            throw new InstallException(string.Join(" ", plan.Problems));
        }
        await ApplyReservedAsync(game, plan.Install.Select(i => i.Mod.Version).ToList(), plan.Uninstall,
            progress, ct, new ProfileActivation(name, plan.Requested.ToList()));
        try
        {
            installer.Recover();
            installer.CompleteProfileActivation(activate);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException)
        {
            throw new InstallException("Profile activation pending. The mod files committed; recheck this installation to finish activation.", e);
        }
    }

    /// <summary>Caller must hold BeginOperation for this game through all record updates.</summary>
    internal async Task<TransactionResult> ApplyReservedAsync(GameInstall game, IReadOnlyList<CatalogVersion> install,
        IReadOnlyList<string> uninstall, IProgress<ModInstallProgress>? progress, CancellationToken ct,
        ProfileActivation? activation = null)
    {
        if (await isGameRunning(game, ct))
        {
            throw GameRunningException.For(game);
        }

        var current = settings.Current;
        var platform = TargetFor(game);
        var targets = install
            .Select(v => (Version: v, Target: v.TargetFor(platform)
                ?? throw new InstallException($"{v.ModReference} {v.Version} has no {platform} build.")))
            .ToList();

        // A large profile is many GB: find out before downloading any of it, not part way through.
        var toDownload = targets
            .Where(t => !File.Exists(downloader.PathFor(t.Version.ModReference, t.Version.Version, t.Target.Platform)))
            .Sum(t => t.Target.Size ?? 0);
        if (toDownload > 0)
        {
            try
            {
                DiskSpace.Require(downloader.ArchivesFolder, toDownload, "the downloads");
            }
            catch (InsufficientSpaceException e)
            {
                throw new InstallException(e.Message, e);
            }
        }

        // Download everything first, so a failed download changes nothing in the game.
        var archives = await Concurrently.MapAsync(targets, PARALLEL_DOWNLOADS, async (t, token) =>
        {
            var reference = t.Version.ModReference;
            var downloadProgress = progress is null
                ? null
                : new InlineProgress<DownloadProgress>(p =>
                    progress.Report(new ModInstallProgress(reference, InstallStage.Downloading, p.BytesReceived, p.TotalBytes)));
            return await downloader.GetAsync(reference, t.Version.Version, t.Target, current.DownloadRetries, downloadProgress, token);
        }, ct);

        var installProgress = progress is null
            ? null
            : new InlineProgress<InstallProgress>(p =>
                progress.Report(new ModInstallProgress(p.Reference, p.Stage, p.FilesDone, p.FilesTotal)));
        var changes = uninstall.Select(r => (ModChange)new ModChange.Uninstall(r))
            .Concat(archives.Select(a => new ModChange.Install(a, platform)))
            .ToList();
        var result = await Task.Run(() => activation is null
            ? InstallerFor(game).Apply(changes, installProgress, ct)
            : InstallerFor(game).ApplyProfile(changes, activation, installProgress, ct), ct);
        _log.LogInformation("Installed {Mods} and uninstalled {Removed} in {Game}",
            result.Installed.Select(m => $"{m.Reference} {m.Version}"), result.Uninstalled, game.GamePath);

        // Only once everything is committed: a rolled-back install keeps its archives for a retry.
        if (!current.KeepDownloadedArchives)
        {
            await Task.Run(() => DeleteArchives(archives), CancellationToken.None);
        }

        return result;
    }

    /// <summary>Removes the mods in one transaction.</summary>
    /// <exception cref="InstallationBusyException">Another operation holds this installation.</exception>
    /// <exception cref="GameRunningException">The game is running.</exception>
    /// <exception cref="InstallException">The removal failed and was rolled back.</exception>
    public async Task<TransactionResult> UninstallAsync(GameInstall game, IReadOnlyList<string> references, CancellationToken ct = default)
    {
        using var operation = BeginOperation(game);
        var result = await Task.Run(
            () => InstallerFor(game).Apply(references.Select(r => (ModChange)new ModChange.Uninstall(r)).ToList(), ct: ct), ct);
        _log.LogInformation("Uninstalled {Mods} from {Game}", result.Uninstalled, game.GamePath);
        return result;
    }

    /// <summary>Mod folders another manager installed in this game.</summary>
    public Task<FoundMods> FindUnmanagedAsync(GameInstall game, CancellationToken ct = default) =>
        Task.Run(() => InstallerFor(game).FindUnmanaged(), ct);

    /// <summary>Takes over mod folders where they are; see <see cref="ModInstaller.Adopt"/>.</summary>
    internal async Task<IReadOnlyList<InstalledMod>> AdoptAsync(GameInstall game, IReadOnlyList<FoundMod> mods, CancellationToken ct = default)
    {
        using var operation = BeginOperation(game);
        return await AdoptReservedAsync(game, mods, ct);
    }

    /// <summary>
    /// <see cref="AdoptAsync"/> for a caller that already holds <see cref="BeginOperation"/>, so it can
    /// also update requested mods and profiles before another operation sees the installation.
    /// </summary>
    internal async Task<IReadOnlyList<InstalledMod>> AdoptReservedAsync(GameInstall game, IReadOnlyList<FoundMod> mods,
        CancellationToken ct)
    {
        var adopted = await Task.Run(() => InstallerFor(game).Adopt(mods, ct), ct);
        _log.LogInformation("Took over {Mods} in {Game}", adopted.Select(m => $"{m.Reference} {m.Version}"), game.GamePath);
        return adopted;
    }

    /// <summary>
    /// Reinstalls each damaged or missing mod at the version that was installed, from a kept archive
    /// when it still matches, otherwise by downloading it again.
    /// </summary>
    public async Task<RepairResult> RepairAsync(GameInstall game, CheckResult check,
        IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        using var operation = BeginOperation(game);
        var installed = await Task.Run(() => InstallerFor(game).Installed, ct);
        var records = check.NeedingRepair
            .Select(m => installed.GetValueOrDefault(m.Reference))
            .OfType<InstalledMod>()
            .ToList();

        var matches = await Concurrently.MapAsync(records, PARALLEL_LOOKUPS, async (record, token) =>
            (await versions.GetVersionsAsync(record.Reference, ct: token)).FirstOrDefault(v => v.Version == record.Version), ct);

        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toInstall = new List<CatalogVersion>();
        for (var i = 0; i < records.Count; i++)
        {
            if (matches[i] is { } match)
            {
                toInstall.Add(match);
            }
            else
            {
                failed[records[i].Reference] = $"Version {records[i].Version} is no longer listed on ficsit.app.";
            }
        }

        if (toInstall.Count == 0)
        {
            return new RepairResult([], failed);
        }

        try
        {
            var result = await ApplyReservedAsync(game, toInstall, [], progress, ct);
            return new RepairResult(result.Installed.Select(m => m.Reference).ToList(), failed);
        }
        catch (Exception e) when (e is InstallException or ArchiveDownloadException or InvalidModArchiveException)
        {
            _log.LogError(e, "Repair failed in {Game}", game.GamePath);
            foreach (var version in toInstall)
            {
                failed[version.ModReference] = e.Message;
            }

            return new RepairResult([], failed);
        }
    }

    private void DeleteArchives(IEnumerable<string> archives)
    {
        foreach (var archive in archives)
        {
            try
            {
                File.Delete(archive);
                var folder = Path.GetDirectoryName(archive)!;
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Only disk space is at stake; the Settings page can still delete it.
                _log.LogWarning(e, "Couldn't delete archive {Archive}", archive);
            }
        }
    }

    /// <summary>
    /// Reserves this installation for a check or mutation until the lease is disposed. Disposing
    /// raises <see cref="Changed"/>. Overlapping operations fail immediately rather than wait.
    /// </summary>
    /// <exception cref="InstallationBusyException">Another operation holds this installation.</exception>
    internal IDisposable BeginOperation(GameInstall game)
    {
        if (!_operations.TryAdd(game.GamePath, 0))
        {
            throw new InstallationBusyException();
        }
        return new OperationLease(() =>
        {
            _operations.TryRemove(game.GamePath, out _);
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private sealed class OperationLease(Action release) : IDisposable
    {
        private Action? _release = release;

        /// <summary>Releases the reservation and notifies refresh listeners exactly once.</summary>
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    /// <summary>Reports on the calling thread; the UI marshals itself.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
