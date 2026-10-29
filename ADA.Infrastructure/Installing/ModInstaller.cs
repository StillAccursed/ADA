using System.Security.Cryptography;
using System.Text.Json;
using ADA.Core.Catalog;
using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Installing;

/// <summary>
/// Installs, updates and uninstalls mods in one game so that <c>Mods</c> only ever changes by
/// renaming whole mod folders: each mod is extracted and verified in the hidden
/// <c>FactoryGame/.ada</c> folder on the game's own drive, then renamed into place.
/// </summary>
/// <remarks>
/// Every commit and rollback step checks where folders actually are rather than trusting the
/// journal alone, because a crash can land between a rename and the journal entry recording it.
/// That makes recovery safe to run any number of times.
/// </remarks>
public sealed class ModInstaller
{
    // Room left on the drive beyond the mod itself, for the journal, records and the filesystem.
    private const long FREE_SPACE_MARGIN = 64L * 1024 * 1024;

    private readonly IGameFiles _files;
    private readonly bool _readBack;
    private readonly Func<bool> _isGameRunning;
    private readonly Action<string> _checkpoint;
    private readonly InstalledModsStore _records;
    private readonly JournalStore _journal;
    private readonly RequestedModsStore _requested;
    private readonly ILogger _log;

    /// <summary>A journal remains for interrupted changes or committed changes awaiting cleanup.</summary>
    internal bool HasInterruptedTransaction => _journal.Exists;

    /// <summary>A committed profile switch still needs its active-profile metadata finalised.</summary>
    internal bool HasPendingProfileActivation => _files.FileExists(GameModFolders.PROFILE_ACTIVATION)
        || _journal.Load() is { Committed: true, ProfileActivation: not null };

    public IReadOnlyDictionary<string, InstalledMod> Installed => _records.Load();

    /// <summary>
    /// Installed mods the user asked for; the rest are there as dependencies. Mods installed before
    /// ADA recorded this all count as asked for, so none is ever removed as unneeded by surprise.
    /// </summary>
    public IReadOnlySet<string> Requested
    {
        get
        {
            var installed = _records.Load();
            var requested = _requested.Load();
            return (requested is null ? installed.Keys : installed.Keys.Where(requested.Contains))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <param name="gameRoot">The folder containing <c>FactoryGame</c>.</param>
    /// <param name="isGameRunning">Checked before every transaction, on the thread running it.</param>
    public ModInstaller(string gameRoot, Func<bool> isGameRunning) : this(gameRoot, isGameRunning, null)
    {
    }

    /// <param name="checkpoint">Called at every step; crash tests throw from it.</param>
    /// <param name="log">Receives cleanup failures after an installation has already committed.</param>
    internal ModInstaller(string gameRoot, Func<bool> isGameRunning, Action<string>? checkpoint, ILogger? log = null)
        : this(new LocalGameFiles(Path.Combine(gameRoot, "FactoryGame")), isGameRunning, checkpoint, log)
    {
    }

    /// <param name="files">The install's files, local or remote. The installer only borrows them.</param>
    /// <param name="checkpoint">Called at every step; crash tests throw from it.</param>
    /// <param name="log">Receives cleanup failures after an installation has already committed.</param>
    /// <param name="readBack">
    /// Read each extracted file back and compare its hash with what was written. False checks only
    /// the size, for slow server connections; the recorded hash is still the one computed while writing.
    /// </param>
    internal ModInstaller(IGameFiles files, Func<bool> isGameRunning, Action<string>? checkpoint, ILogger? log = null,
        bool readBack = true)
    {
        _files = files;
        _readBack = readBack;
        _isGameRunning = isGameRunning;
        _checkpoint = checkpoint ?? (_ => { });
        _records = new InstalledModsStore(files, GameModFolders.INSTALLED_RECORD);
        _journal = new JournalStore(files, GameModFolders.JOURNAL);
        _requested = new RequestedModsStore(files, GameModFolders.REQUESTED_RECORD);
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Records which mods the user asked for. Callers save additions before installing and removals
    /// after, so a crash in between never leaves a wanted mod looking unneeded.
    /// </summary>
    internal void SaveRequested(IEnumerable<string> references)
    {
        GameModFolders.EnsureWorkFolder(_files);
        _requested.Save(references);
    }

    /// <summary>
    /// Applies the changes in order (callers put dependencies first). Each mod goes live as soon as
    /// it is verified. Failures before commitment undo every change. After commitment, cleanup
    /// failures are logged and retried by recovery without undoing the installed changes.
    /// </summary>
    /// <exception cref="GameRunningException">The game is running.</exception>
    /// <exception cref="InvalidModArchiveException">An archive fails validation; nothing was changed.</exception>
    /// <exception cref="InstallException">A change failed before commitment and was rolled back.</exception>
    internal TransactionResult Apply(IReadOnlyList<ModChange> changes, IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
        => ApplyCore(changes, progress, ct, null);

    /// <summary>Applies a whole profile; uncommitted recovery rolls back the entire switch.</summary>
    internal TransactionResult ApplyProfile(IReadOnlyList<ModChange> changes, ProfileActivation activation,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
        => ApplyCore(changes, progress, ct, activation);

    private TransactionResult ApplyCore(IReadOnlyList<ModChange> changes, IProgress<InstallProgress>? progress,
        CancellationToken ct, ProfileActivation? activation)
    {
        if (_isGameRunning())
        {
            throw new GameRunningException();
        }

        GameModFolders.EnsureWorkFolder(_files);
        using var _ = AcquireLock();
        if (_journal.Exists)
        {
            RecoverLocked();
        }
        ThrowIfProfileActivationPending("changing mods");

        var installed = _records.Load();
        var archives = new List<ModArchive>();
        try
        {
            // Open and validate everything before touching anything.
            var plan = Plan(changes, installed, archives);
            CheckFreeSpace(archives);

            var txn = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..23];
            _files.CreateDirectory(GameModFolders.Transaction(txn));
            var journal = new Journal(txn, DateTimeOffset.UtcNow, false, plan.Select(p => p.Step).ToList(),
                ProfileActivation: activation);
            _journal.Save(journal);
            _checkpoint("journal-created");

            try
            {
                for (var i = 0; i < plan.Count; i++)
                {
                    var (step, archive) = plan[i];
                    if (archive is not null)
                    {
                        var files = Extract(archive, GameModFolders.NewStaged(txn, step.Reference), step.Reference, progress, ct);
                        var record = new InstalledMod(step.Reference, archive.Descriptor.Version, archive.Descriptor.GameFeature,
                            step.NewLocation!, DateTimeOffset.UtcNow, files);
                        step = journal.Steps[i] = step with { State = StepState.Ready, NewRecord = record };
                        _journal.Save(journal);
                        _checkpoint($"ready:{step.Reference}");
                    }

                    progress?.Report(new InstallProgress(step.Reference, InstallStage.Committing, 0, 0));
                    Commit(txn, step, installed);
                    journal.Steps[i] = step with { State = StepState.Done };
                    _journal.Save(journal);
                    _checkpoint($"done:{step.Reference}");
                }

                // Persist the final outcome while every old version is still available for rollback.
                _journal.Save(journal with { Committed = true });
            }
            catch (Exception e) when (e is not SimulatedCrashException)
            {
                RollBack(journal, installed);
                throw e is OperationCanceledException
                    ? new InstallException("Cancelled; no mods were changed.", e)
                    : new InstallException($"Install failed and every change was undone: {e.Message}", e);
            }

            // Cleanup can delete the old versions, so it must never enter the rollback handler.
            try
            {
                _checkpoint("transaction-committed");
                SaveProfileActivation(activation);
                Finish(txn);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(e, "Transaction {Transaction} committed; cleanup will be retried during recovery", txn);
            }

            return new TransactionResult(
                journal.Steps.Where(s => s.NewRecord is not null).Select(s => s.NewRecord!).ToList(),
                journal.Steps.Where(s => s.NewLocation is null).Select(s => s.Reference).ToList());
        }
        finally
        {
            foreach (var archive in archives)
            {
                archive.Dispose();
            }
        }
    }

    /// <summary>
    /// Finishes or undoes a transaction a crash interrupted. Run on launch, before anything else
    /// touches the game's mods. A committed journal only retries cleanup; cleanup failures leave
    /// the journal for a later retry and do not change live mod folders or installed records.
    /// </summary>
    internal RecoveryResult Recover()
    {
        // ADA has never changed this game: nothing to recover, and nothing to create on launch.
        if (!_files.DirectoryExists(GameModFolders.WORK))
        {
            return RecoveryResult.CLEAN;
        }

        GameModFolders.EnsureWorkFolder(_files);
        using var _ = AcquireLock();
        return RecoverLocked();
    }

    /// <summary>
    /// Refuses <paramref name="action"/> while an interrupted transaction or a committed profile
    /// switch still needs finishing; the next check (<see cref="Recover"/> then profile
    /// reconciliation) settles both.
    /// </summary>
    /// <param name="action">What the caller is about to do, completing "… before {action}."</param>
    /// <exception cref="InstallException">Recovery or profile activation is pending.</exception>
    internal void ThrowIfUnsettled(string action)
    {
        if (HasInterruptedTransaction || HasPendingProfileActivation)
        {
            throw new InstallException($"Recheck this installation to finish pending recovery before {action}.");
        }
    }

    /// <summary>
    /// Finalises committed profile metadata under the game's lock. The marker remains if the
    /// callback or requested-mod write fails; retrying is safe and precedes profile recording.
    /// </summary>
    internal void CompleteProfileActivation(Action<string> activate)
    {
        if (!_files.FileExists(GameModFolders.PROFILE_ACTIVATION))
        {
            return;
        }
        using var operationLock = AcquireLock();
        if (!_files.FileExists(GameModFolders.PROFILE_ACTIVATION))
        {
            return;
        }
        if (_journal.Exists)
        {
            throw new InstallException("Profile activation pending; transaction cleanup must finish first.");
        }
        ProfileActivation activation;
        using (var stream = _files.OpenRead(GameModFolders.PROFILE_ACTIVATION))
        {
            activation = JsonSerializer.Deserialize(stream, InstallJsonContext.Default.ProfileActivation)
                ?? throw new InstallException("The pending profile activation record is unreadable.");
        }
        activate(activation.Name);
        _requested.Save(activation.Requested);
        _checkpoint("profile-activated");
        _files.DeleteFile(GameModFolders.PROFILE_ACTIVATION);
    }

    /// <summary>
    /// <see cref="ThrowIfUnsettled"/> for callers holding the game lock that have just recovered: a
    /// journal that cleanup could not remove does not block them, only a pending profile activation.
    /// </summary>
    private void ThrowIfProfileActivationPending(string action)
    {
        if (HasPendingProfileActivation)
        {
            throw new InstallException($"Recheck this installation to finish pending recovery before {action}.");
        }
    }

    private void SaveProfileActivation(ProfileActivation? activation)
    {
        if (activation is not null)
        {
            AtomicFile.Write(_files, GameModFolders.PROFILE_ACTIVATION,
                stream => JsonSerializer.Serialize(stream, activation, InstallJsonContext.Default.ProfileActivation));
            _checkpoint("profile-activation-pending");
        }
    }

    /// <summary>
    /// Compares installed mods with what was recorded at install. The fast check (default) compares
    /// sizes and modification times and only hashes files that differ; <paramref name="full"/>
    /// hashes every file.
    /// </summary>
    public CheckResult Check(bool full = false)
    {
        var installed = _records.Load();
        var results = new List<ModCheck>();
        foreach (var mod in installed.Values.OrderBy(m => m.Reference, StringComparer.OrdinalIgnoreCase))
        {
            var folder = mod.Location;
            if (!_files.DirectoryExists(folder))
            {
                results.Add(new ModCheck(mod.Reference, ModHealth.Missing, [$"{mod.Location} is missing."]));
                continue;
            }

            var problems = new List<string>();
            foreach (var file in mod.Files)
            {
                var path = GamePaths.Combine(folder, file.Path);
                var info = _files.FileInfo(path);
                if (info is null)
                {
                    problems.Add($"{file.Path} is missing.");
                }
                else if (info.Length != file.Size)
                {
                    problems.Add($"{file.Path} has the wrong size.");
                }
                else if ((full || info.LastWriteUtc != file.LastWriteUtc) && HashFile(path) != file.Sha256)
                {
                    problems.Add($"{file.Path} has changed.");
                }
            }
            results.Add(new ModCheck(mod.Reference, problems.Count == 0 ? ModHealth.Ok : ModHealth.Damaged, problems));
        }

        var known = new HashSet<string>(installed.Values.Select(m => m.Location), StringComparer.OrdinalIgnoreCase);
        var unmanaged = new List<string>();
        foreach (var folder in new[] { GameModFolders.MODS, GameModFolders.GAME_FEATURES })
        {
            if (!_files.DirectoryExists(folder))
            {
                continue;
            }

            foreach (var name in _files.DirectoryNames(folder))
            {
                var location = GamePaths.Combine(folder, name);
                if (location != GameModFolders.GAME_FEATURES && !known.Contains(location))
                {
                    unmanaged.Add(location);
                }
            }
        }
        unmanaged.Sort(StringComparer.OrdinalIgnoreCase);

        return new CheckResult(results, unmanaged);
    }

    /// <summary>
    /// Mod folders ADA didn't install, with their name and version from each <c>.uplugin</c>.
    /// Only reads; nothing outside the game's mod folders is looked at.
    /// </summary>
    /// <param name="platform">
    /// The game's platform: a mod whose libraries are all for another platform (a client build copied
    /// onto a server) is listed in <see cref="FoundMods.WrongBuild"/> instead. Null skips that check.
    /// </param>
    internal FoundMods FindUnmanaged(TargetPlatform? platform = null)
    {
        var mods = new List<FoundMod>();
        var unreadable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wrongBuild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var references = new HashSet<string>(_records.Load().Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var location in Check().Unmanaged)
        {
            var reference = location[(location.LastIndexOf('/') + 1)..];
            var descriptor = GamePaths.Combine(location, reference + ".uplugin");
            if (!ModIdentifiers.IsValidReference(reference) || !_files.FileExists(descriptor))
            {
                unreadable[location] = "Not a mod: there's no .uplugin file named after the folder.";
                continue;
            }

            try
            {
                using var stream = _files.OpenRead(descriptor);
                var plugin = UPluginDescriptor.Parse(reference, stream);
                if (platform is { } target && WrongBuild(location, target) is { } wrong)
                {
                    wrongBuild[location] = wrong;
                    continue;
                }
                if (!references.Add(reference))
                {
                    unreadable[location] = $"{reference} is installed in another folder too.";
                    continue;
                }
                mods.Add(new FoundMod(reference, plugin.Version, plugin.FriendlyName, location,
                    plugin.Dependencies.Where(d => !d.Optional).Select(d => d.Name).ToList()));
            }
            catch (Exception e) when (e is InvalidModArchiveException or IOException or UnauthorizedAccessException)
            {
                unreadable[location] = e.Message;
            }
        }
        return new FoundMods(mods, unreadable, wrongBuild);
    }

    /// <summary>
    /// Whether a folder for this mod is in the game's <c>Mods</c> or <c>Mods/GameFeatures</c>,
    /// installed by ADA or not. A network call for a server.
    /// </summary>
    internal bool HasModFolder(string reference) =>
        _files.DirectoryExists(GameModFolders.LocationFor(reference, gameFeature: false))
        || _files.DirectoryExists(GameModFolders.LocationFor(reference, gameFeature: true));

    /// <summary>
    /// Why the mod folder's libraries are for another platform, or null when they suit this one or it
    /// has none (a content-only mod runs anywhere). Reads only folder listings.
    /// </summary>
    private string? WrongBuild(string location, TargetPlatform platform)
    {
        var libraries = ModBinaries.FOLDERS
            .Select(folder => GamePaths.Combine(location, $"Binaries/{folder}"))
            .Where(_files.DirectoryExists)
            .SelectMany(folder => _files.Files(folder).Select(f => f.Path[(f.Path.LastIndexOf('/') + 1)..]))
            .Where(ModBinaries.IsLibrary)
            .ToList();
        if (libraries.Count == 0 || libraries.Any(name => ModBinaries.IsFor(name, platform)))
        {
            return null;
        }
        return platform == TargetPlatform.Windows
            ? "Server build, won't run in the game. Remove it, then install it through ADA."
            : "Client build, won't run on this server. Remove it, then install it through ADA.";
    }

    /// <summary>
    /// Mods that installed mods require (from each one's <c>.uplugin</c>, optional ones left out) but
    /// that aren't installed. Cheap and local; the resolver decides what actually needs installing,
    /// since a <c>.uplugin</c> can also name engine plugins that ship with the game.
    /// </summary>
    /// <returns>Missing mod → the installed mods that need it.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MissingDependencies()
    {
        var installed = _records.Load();
        var missing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installed.Values)
        {
            var descriptor = GamePaths.Combine(mod.Location, mod.Reference + ".uplugin");
            UPluginDescriptor plugin;
            try
            {
                using var stream = _files.OpenRead(descriptor);
                plugin = UPluginDescriptor.Parse(mod.Reference, stream);
            }
            catch (Exception e) when (e is InvalidModArchiveException or IOException or UnauthorizedAccessException)
            {
                continue; // The fast check reports damaged mods.
            }

            foreach (var dependency in plugin.Dependencies.Where(d => !d.Optional && !installed.ContainsKey(d.Name)))
            {
                if (!missing.TryGetValue(dependency.Name, out var neededBy))
                {
                    missing[dependency.Name] = neededBy = [];
                }

                neededBy.Add(mod.Reference);
            }
        }
        return missing.ToDictionary(m => m.Key, m => (IReadOnlyList<string>)m.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Takes over mod folders another manager installed, where they are: their files are hashed and
    /// recorded as if ADA had installed them, so check, repair, update and uninstall work. No
    /// file is moved or changed, so this is safe while the game runs.
    /// </summary>
    /// <returns>What was recorded. Folders that changed since <see cref="FindUnmanaged"/> are skipped.</returns>
    internal IReadOnlyList<InstalledMod> Adopt(IReadOnlyList<FoundMod> mods, CancellationToken ct = default)
    {
        GameModFolders.EnsureWorkFolder(_files);
        using var _ = AcquireLock();
        if (_journal.Exists)
        {
            RecoverLocked();
        }
        ThrowIfProfileActivationPending("importing mods");

        var installed = _records.Load();
        var current = FindUnmanaged().Mods.ToDictionary(m => m.Location, StringComparer.OrdinalIgnoreCase);
        var adopted = new List<InstalledMod>();
        foreach (var mod in mods)
        {
            if (!current.TryGetValue(mod.Location, out var now) || now.Version != mod.Version || installed.ContainsKey(mod.Reference))
            {
                continue;
            }

            var files = new List<InstalledFile>();
            foreach (var info in _files.Files(mod.Location))
            {
                ct.ThrowIfCancellationRequested();
                files.Add(new InstalledFile(info.Path, info.Length, HashFile(GamePaths.Combine(mod.Location, info.Path)), info.LastWriteUtc));
            }
            var record = new InstalledMod(mod.Reference, mod.Version, mod.Location.StartsWith("Mods/GameFeatures/", StringComparison.OrdinalIgnoreCase),
                mod.Location, DateTimeOffset.UtcNow, files);
            installed[mod.Reference] = record;
            adopted.Add(record);
        }

        // One write: either every folder is taken over, or (after a crash) none is.
        if (adopted.Count > 0)
        {
            _records.Save(installed);
        }

        return adopted;
    }

    private sealed record PlannedStep(JournalStep Step, ModArchive? Archive);

    private List<PlannedStep> Plan(IReadOnlyList<ModChange> changes, Dictionary<string, InstalledMod> installed,
        List<ModArchive> archives)
    {
        var plan = new List<PlannedStep>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            switch (change)
            {
                case ModChange.Install install:
                {
                    var archive = ModArchive.Open(install.ArchivePath, install.Platform, install.IncludeDebugSymbols);
                    archives.Add(archive);
                    var reference = archive.Descriptor.Reference;
                    var old = installed.GetValueOrDefault(reference);
                    var location = GameModFolders.LocationFor(reference, archive.Descriptor.GameFeature);

                    // Never replace a folder ADA did not put there (another tool's or a manual install).
                    foreach (var candidate in new[] { location, GameModFolders.LocationFor(reference, !archive.Descriptor.GameFeature) })
                    {
                        if (candidate != old?.Location && _files.DirectoryExists(candidate))
                            {
                                throw new InstallException($"{candidate} already exists and was not installed by ADA.");
                            }
                        }

                    plan.Add(new PlannedStep(new JournalStep(reference, old?.Location, location, StepState.Planned, old, null), archive));
                    break;
                }
                case ModChange.Uninstall uninstall:
                {
                    var old = installed.GetValueOrDefault(uninstall.Reference)
                              ?? throw new InstallException($"{uninstall.Reference} was not installed by ADA, so it is left alone.");
                    plan.Add(new PlannedStep(new JournalStep(old.Reference, old.Location, null, StepState.Planned, old, null), null));
                    break;
                }
            }

            if (!seen.Add(plan[^1].Step.Reference))
            {
                throw new InstallException($"{plan[^1].Step.Reference} appears more than once in one transaction.");
            }
        }
        return plan;
    }

    /// <summary>Extracts into the hidden folder, verifying every file as it lands.</summary>
    private List<InstalledFile> Extract(ModArchive archive, string destination, string reference,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (_files.DirectoryExists(destination))
        {
            _files.DeleteDirectory(destination);
        }

        _files.CreateDirectory(destination);

        var files = new List<InstalledFile>(archive.Entries.Count);
        for (var i = 0; i < archive.Entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = archive.Entries[i];
            if (!GamePaths.IsSafe(entry.RelativePath) || entry.RelativePath.Length == 0)
            {
                throw new InvalidModArchiveException($"Unsafe path in archive: '{entry.RelativePath}'.");
            }

            var target = GamePaths.Combine(destination, entry.RelativePath);
            _files.CreateDirectory(GamePaths.Parent(target));

            // Hash what we write as we write it, make it durable, then read it back and compare.
            string expected;
            long written = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var source = entry.Zip.Open())
                using (var file = _files.CreateNew(target))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer)) > 0)
                    {
                        // The archive's declared sizes are what the free-space and size checks used;
                        // an entry that inflates past its own is a zip bomb or corrupt, so stop at once.
                        if (written + read > entry.Size)
                        {
                            throw new InvalidModArchiveException($"{entry.RelativePath} extracts to more than its declared {entry.Size:N0} bytes.");
                        }

                        hash.AppendData(buffer, 0, read);
                        file.Write(buffer, 0, read);
                        written += read;
                    }
                }
                expected = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (written != entry.Size)
            {
                throw new InstallException($"{reference}: {entry.RelativePath} extracted {written:N0} of {entry.Size:N0} bytes.");
            }

            var info = _files.FileInfo(target)!;
            if (info.Length != written)
            {
                throw new InstallException($"{reference}: {entry.RelativePath} is {info.Length:N0} bytes on disk, not the {written:N0} written.");
            }
            if (_readBack && HashFile(target) != expected)
            {
                throw new InstallException($"{reference}: {entry.RelativePath} did not read back the same as it was written.");
            }

            files.Add(new InstalledFile(entry.RelativePath, info.Length, expected, info.LastWriteUtc));
            progress?.Report(new InstallProgress(reference, InstallStage.Extracting, i + 1, archive.Entries.Count));
            _checkpoint($"extracted:{reference}:{i}");
        }
        return files;
    }

    /// <summary>Makes one step live. Idempotent: safe to repeat after a crash at any point inside it.</summary>
    private void Commit(string txn, JournalStep step, Dictionary<string, InstalledMod> installed)
    {
        var newStaged = GameModFolders.NewStaged(txn, step.Reference);
        var oldStaged = GameModFolders.OldStaged(txn, step.Reference);
        var oldLive = step.OldLocation;

        if (step.NewLocation is null)
        {
            // Uninstall: move the mod out of Mods; it is deleted with the transaction folder.
            if (oldLive is not null && _files.DirectoryExists(oldLive) && !_files.DirectoryExists(oldStaged))
            {
                MoveFolder(oldLive, oldStaged, $"moved-out:{step.Reference}");
            }

            installed.Remove(step.Reference);
        }
        else
        {
            var newLive = step.NewLocation;
            if (_files.DirectoryExists(newStaged))
            {
                if (oldLive is not null && _files.DirectoryExists(oldLive) && !_files.DirectoryExists(oldStaged))
                {
                    MoveFolder(oldLive, oldStaged, $"moved-out:{step.Reference}");
                }

                if (_files.DirectoryExists(newLive))
                {
                    throw new InstallException($"{step.NewLocation} appeared while installing; refusing to overwrite it.");
                }

                _files.CreateDirectory(GamePaths.Parent(newLive));
                MoveFolder(newStaged, newLive, $"moved-in:{step.Reference}");
            }
            else if (!_files.DirectoryExists(newLive))
            {
                throw new InstallException($"{step.Reference}: the verified files are missing from both the work folder and Mods.");
            }
            installed[step.Reference] = step.NewRecord!;
        }

        _records.Save(installed);
        _checkpoint($"recorded:{step.Reference}");
    }

    /// <summary>Undoes every step of a transaction, newest first. Idempotent, like <see cref="Commit"/>.</summary>
    private RecoveryResult RollBack(Journal journal, Dictionary<string, InstalledMod> installed)
    {
        var txn = journal.TransactionId;
        if (!journal.RollingBack)
        {
            journal = journal with { RollingBack = true };
            _journal.Save(journal);
            _checkpoint("rollback-started");
        }

        foreach (var step in Enumerable.Reverse(journal.Steps))
        {
            var newStaged = GameModFolders.NewStaged(txn, step.Reference);
            var oldStaged = GameModFolders.OldStaged(txn, step.Reference);
            var trash = GameModFolders.Trash(txn, step.Reference);

            // Take the new version out of Mods if it went live. When old and new share a location,
            // the folder there is only the new one while the old one is still parked in old/.
            if (step.NewLocation is not null && step.State != StepState.Planned)
            {
                var newLive = step.NewLocation;
                var newIsLive = !_files.DirectoryExists(newStaged) && !_files.DirectoryExists(trash) && _files.DirectoryExists(newLive)
                                && (step.OldLocation != step.NewLocation || _files.DirectoryExists(oldStaged));
                if (newIsLive)
                {
                    MoveFolder(newLive, trash, $"rolled-out:{step.Reference}");
                }
            }

            // Put the old version back.
            if (step.OldLocation is not null)
            {
                var oldLive = step.OldLocation;
                if (_files.DirectoryExists(oldStaged) && !_files.DirectoryExists(oldLive))
                {
                    _files.CreateDirectory(GamePaths.Parent(oldLive));
                    MoveFolder(oldStaged, oldLive, $"restored:{step.Reference}");
                }
            }

            if (step.OldRecord is not null)
            {
                installed[step.Reference] = step.OldRecord;
            }
            else
            {
                installed.Remove(step.Reference);
            }
        }

        _records.Save(installed);
        _checkpoint("rollback-recorded");
        Finish(txn);
        return new RecoveryResult(true, [], [], journal.Steps.Select(s => s.Reference).ToList());
    }

    private RecoveryResult RecoverLocked()
    {
        var journal = _journal.Load();
        if (journal is null)
        {
            // A crash after the journal was removed but before the work folder was cleaned.
            foreach (var stray in _files.DirectoryNames(GameModFolders.WORK).Where(n => n.StartsWith(GameModFolders.TRANSACTION_PREFIX, StringComparison.Ordinal)).ToList())
            {
                _files.DeleteDirectory(GamePaths.Combine(GameModFolders.WORK, stray));
            }

            return RecoveryResult.CLEAN;
        }

        if (journal.Committed)
        {
            // Old versions may already be deleted. The live folders and records are final.
            SaveProfileActivation(journal.ProfileActivation);
            Finish(journal.TransactionId);
            return new RecoveryResult(true,
                journal.Steps.Where(s => s.NewLocation is null || s.State != StepState.Planned).Select(s => s.Reference).ToList(),
                journal.Steps.Where(s => s.NewLocation is not null && s.State == StepState.Planned).Select(s => s.Reference).ToList(),
                []);
        }

        var installed = _records.Load();
        if (journal.RollingBack || journal.ProfileActivation is not null)
        {
            return RollBack(journal, installed);
        }

        var rolledForward = new List<string>();
        var discarded = new List<string>();
        foreach (var step in journal.Steps)
        {
            // An install that never finished verifying is dropped: Mods was not touched for it.
            if (step.NewLocation is not null && step.State == StepState.Planned)
            {
                discarded.Add(step.Reference);
                continue;
            }

            // Verified installs, and all uninstalls, are completed.
            Commit(journal.TransactionId, step, installed);
            rolledForward.Add(step.Reference);
        }

        // Preserve the existing per-mod recovery outcome before discarding any old versions.
        _journal.Save(journal with { Committed = true });
        _checkpoint("transaction-committed");
        Finish(journal.TransactionId);
        return new RecoveryResult(true, rolledForward, discarded, []);
    }

    /// <summary>
    /// Removes the working folder before deleting the journal, so failed cleanup can be retried.
    /// Callers must finalize the installation or rollback before discarding its staged files.
    /// </summary>
    private void Finish(string txn)
    {
        _checkpoint("cleanup-started");
        var folder = GameModFolders.Transaction(txn);
        if (_files.DirectoryExists(folder))
        {
            _files.DeleteDirectory(folder);
        }

        _checkpoint("work-cleaned");
        _journal.Delete();
        _checkpoint("journal-removed");
    }

    private void MoveFolder(string from, string to, string checkpoint)
    {
        _files.CreateDirectory(GamePaths.Parent(to));
        _files.MoveDirectory(from, to);
        _checkpoint(checkpoint);
    }

    private void CheckFreeSpace(IReadOnlyList<ModArchive> archives)
    {
        try
        {
            _files.RequireFreeSpace(archives.Sum(a => a.UncompressedBytes) + FREE_SPACE_MARGIN, "these mods");
        }
        catch (InsufficientSpaceException e)
        {
            throw new InstallException(e.Message, e);
        }
    }

    /// <summary>Stops two ADA windows (or, for a server, two PCs) changing the same game at once.</summary>
    private IDisposable AcquireLock()
    {
        try
        {
            return _files.AcquireLock(GameModFolders.LOCK);
        }
        catch (LockHeldException e)
        {
            throw new InstallException(e.Message, e);
        }
        catch (IOException e)
        {
            throw new InstallException("Another ADA window is changing this game's mods.", e);
        }
    }

    private string HashFile(string path)
    {
        using var stream = _files.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
