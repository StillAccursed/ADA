using Overlord.Core.Catalog;
using Overlord.Core.Games;
using Overlord.Infrastructure.Downloads;
using Overlord.Infrastructure.Settings;

namespace Overlord.Infrastructure.Installing;

/// <param name="Stage">"Downloading", "Extracting" or "Committing".</param>
/// <param name="Done">Bytes downloaded, or files extracted.</param>
public sealed record ModInstallProgress(string Reference, string Stage, long Done, long? Total);

/// <param name="RecoveryDeferred">An interrupted transaction was found, but the game is running; it is finished next time.</param>
public sealed record GameModsStatus(RecoveryResult Recovery, bool RecoveryDeferred, CheckResult Check);

/// <param name="Failed">Mod → why it could not be repaired.</param>
public sealed record RepairResult(IReadOnlyList<string> Repaired, IReadOnlyDictionary<string, string> Failed);

/// <summary>
/// The app's entry point for changing a game's mods: downloads verified archives (with the user's
/// retry setting), hands them to <see cref="ModInstaller"/>, then applies the "Keep downloaded
/// archives" setting. Also runs launch-time recovery and checks, and repairs damaged mods.
/// </summary>
public sealed class ModInstallService(
    SettingsStore settings,
    ArchiveDownloader downloader,
    Func<string, CancellationToken, Task<IReadOnlyList<CatalogVersion>>> getVersions,
    Func<bool> isGameRunning)
{
    /// <summary>The build a game needs: clients always use Windows builds (also under CrossOver and Proton).</summary>
    public static TargetPlatform TargetFor(GameInstall game) => game.Kind switch
    {
        GameKind.DedicatedServer when game.Host == GameHost.Linux => TargetPlatform.LinuxServer,
        GameKind.DedicatedServer => TargetPlatform.WindowsServer,
        _ => TargetPlatform.Windows,
    };

    public ModInstaller InstallerFor(GameInstall game) => new(game.GamePath, isGameRunning);

    /// <summary>
    /// Run on launch for each detected game: finish or undo an interrupted transaction, then the
    /// fast check. Recovery waits if the game is running, because it would move folders the game has open.
    /// </summary>
    public Task<GameModsStatus> RecoverAndCheckAsync(GameInstall game, CancellationToken ct = default) => Task.Run(() =>
    {
        var installer = InstallerFor(game);
        var deferred = installer.HasInterruptedTransaction && isGameRunning();
        var recovery = deferred ? RecoveryResult.Clean : installer.Recover();
        return new GameModsStatus(recovery, deferred, installer.Check());
    }, ct);

    /// <summary>
    /// Downloads (or reuses kept archives for) the given versions, then installs them in the given
    /// order in one transaction. Callers put dependencies first.
    /// </summary>
    /// <exception cref="GameRunningException">Checked before anything is downloaded.</exception>
    /// <exception cref="ArchiveDownloadException">A download failed every attempt; nothing was installed.</exception>
    /// <exception cref="InstallException">The install failed and was rolled back.</exception>
    public async Task<TransactionResult> InstallAsync(GameInstall game, IReadOnlyList<CatalogVersion> versions,
        IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (isGameRunning())
            throw new GameRunningException();

        var current = settings.Load();
        var platform = TargetFor(game);

        // Download everything first, so a failed download changes nothing in the game.
        var archives = new List<string>(versions.Count);
        foreach (var version in versions)
        {
            var target = version.TargetFor(platform)
                         ?? throw new InstallException($"{version.ModReference} {version.Version} has no {platform} build.");
            var reference = version.ModReference;
            var downloadProgress = progress is null
                ? null
                : new InlineProgress<DownloadProgress>(p =>
                    progress.Report(new ModInstallProgress(reference, "Downloading", p.BytesReceived, p.TotalBytes)));
            archives.Add(await downloader.GetAsync(reference, version.Version, target, current.DownloadRetries, downloadProgress, ct));
        }

        var installProgress = progress is null
            ? null
            : new InlineProgress<InstallProgress>(p =>
                progress.Report(new ModInstallProgress(p.Reference, p.Stage, p.FilesDone, p.FilesTotal)));
        var changes = archives.Select(a => (ModChange)new ModChange.Install(a, platform)).ToList();
        var result = await Task.Run(() => InstallerFor(game).Apply(changes, installProgress, ct), ct);

        // Only once everything is committed: a rolled-back install keeps its archives for a retry.
        if (!current.KeepDownloadedArchives)
            DeleteArchives(archives);

        return result;
    }

    public Task<TransactionResult> UninstallAsync(GameInstall game, IReadOnlyList<string> references, CancellationToken ct = default) =>
        Task.Run(() => InstallerFor(game).Apply(references.Select(r => (ModChange)new ModChange.Uninstall(r)).ToList(), ct: ct), ct);

    /// <summary>
    /// Reinstalls each damaged or missing mod at the version that was installed, from a kept archive
    /// when it still matches, otherwise by downloading it again.
    /// </summary>
    public async Task<RepairResult> RepairAsync(GameInstall game, CheckResult check,
        IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        var installed = InstallerFor(game).Installed;
        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toInstall = new List<CatalogVersion>();

        foreach (var mod in check.NeedingRepair)
        {
            if (!installed.TryGetValue(mod.Reference, out var record))
                continue;

            var versions = await getVersions(record.Reference, ct);
            var match = versions.FirstOrDefault(v => v.Version == record.Version);
            if (match is null)
                failed[record.Reference] = $"Version {record.Version} is no longer listed on ficsit.app.";
            else
                toInstall.Add(match);
        }

        if (toInstall.Count == 0)
            return new RepairResult([], failed);

        try
        {
            var result = await InstallAsync(game, toInstall, progress, ct);
            return new RepairResult(result.Installed.Select(m => m.Reference).ToList(), failed);
        }
        catch (Exception e) when (e is InstallException or ArchiveDownloadException or InvalidModArchiveException)
        {
            foreach (var version in toInstall)
                failed[version.ModReference] = e.Message;
            return new RepairResult([], failed);
        }
    }

    private static void DeleteArchives(IEnumerable<string> archives)
    {
        foreach (var archive in archives)
        {
            try
            {
                File.Delete(archive);
                var folder = Path.GetDirectoryName(archive)!;
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (IOException)
            {
                // Only disk space is at stake; the Settings page can still delete it.
            }
        }
    }

    /// <summary>Reports on the calling thread; the UI marshals itself.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
