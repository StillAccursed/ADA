using System.Security.Cryptography;
using Overlord.Core.Catalog;
using Overlord.Infrastructure.IO;

namespace Overlord.Infrastructure.Installing;

/// <summary>A change to apply to one game's mods. An install over an installed mod is an update.</summary>
public abstract record ModChange
{
    /// <param name="ArchivePath">A verified <c>.smod</c> (see <c>ArchiveDownloader</c>).</param>
    public sealed record Install(string ArchivePath, TargetPlatform Platform, bool IncludeDebugSymbols = true) : ModChange;

    public sealed record Uninstall(string Reference) : ModChange;
}

public enum InstallStage
{
    Downloading,
    Extracting,
    Committing,
}

public sealed record InstallProgress(string Reference, InstallStage Stage, int FilesDone, int FilesTotal);

public class InstallException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GameRunningException()
    : InstallException("Satisfactory is running. Close the game before changing mods; it locks mod files.");

public sealed record TransactionResult(IReadOnlyList<InstalledMod> Installed, IReadOnlyList<string> Uninstalled);

/// <param name="RolledForward">Verified changes that were completed.</param>
/// <param name="Discarded">Unverified changes that were dropped; those mods are as they were.</param>
public sealed record RecoveryResult(
    bool HadInterruptedTransaction,
    IReadOnlyList<string> RolledForward,
    IReadOnlyList<string> Discarded,
    IReadOnlyList<string> RolledBack)
{
    public static readonly RecoveryResult Clean = new(false, [], [], []);
}

public enum ModHealth
{
    Ok,
    Damaged,
    Missing,
}

public sealed record ModCheck(string Reference, ModHealth Health, IReadOnlyList<string> Problems);

/// <param name="Unmanaged">Mod folders Overlord did not install; left alone.</param>
public sealed record CheckResult(IReadOnlyList<ModCheck> Mods, IReadOnlyList<string> Unmanaged)
{
    public IEnumerable<ModCheck> NeedingRepair => Mods.Where(m => m.Health != ModHealth.Ok);
}

/// <summary>Thrown by test checkpoints to stop the engine dead, as a crash or power cut would.</summary>
internal sealed class SimulatedCrashException(string checkpoint) : Exception("Simulated crash at " + checkpoint);

/// <summary>
/// Installs, updates and uninstalls mods in one game so that <c>Mods</c> only ever changes by
/// renaming whole mod folders: each mod is extracted and verified in the hidden
/// <c>FactoryGame/.overlord</c> folder on the game's own drive, then renamed into place.
/// </summary>
/// <remarks>
/// Every commit and rollback step checks where folders actually are rather than trusting the
/// journal alone, because a crash can land between a rename and the journal entry recording it.
/// That makes recovery safe to run any number of times.
/// </remarks>
public sealed class ModInstaller
{
    // Room left on the drive beyond the mod itself, for the journal, records and the filesystem.
    private const long FreeSpaceMargin = 64L * 1024 * 1024;

    private readonly GameModFolders _folders;
    private readonly Func<bool> _isGameRunning;
    private readonly Action<string> _checkpoint;
    private readonly InstalledModsStore _records;
    private readonly JournalStore _journal;
    private readonly RequestedModsStore _requested;

    /// <param name="gameRoot">The folder containing <c>FactoryGame</c>.</param>
    /// <param name="isGameRunning">Checked before every transaction, on the thread running it.</param>
    public ModInstaller(string gameRoot, Func<bool> isGameRunning) : this(gameRoot, isGameRunning, null)
    {
    }

    /// <param name="checkpoint">Called at every step; crash tests throw from it.</param>
    internal ModInstaller(string gameRoot, Func<bool> isGameRunning, Action<string>? checkpoint)
    {
        _folders = new GameModFolders(gameRoot);
        _isGameRunning = isGameRunning;
        _checkpoint = checkpoint ?? (_ => { });
        _records = new InstalledModsStore(_folders.InstalledRecord);
        _journal = new JournalStore(_folders.Journal);
        _requested = new RequestedModsStore(_folders.RequestedRecord);
    }

    public bool HasInterruptedTransaction => _journal.Exists;

    public IReadOnlyDictionary<string, InstalledMod> Installed => _records.Load();

    /// <summary>
    /// Installed mods the user asked for; the rest are there as dependencies. Mods installed before
    /// Overlord recorded this all count as asked for, so none is ever removed as unneeded by surprise.
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

    /// <summary>
    /// Records which mods the user asked for. Callers save additions before installing and removals
    /// after, so a crash in between never leaves a wanted mod looking unneeded.
    /// </summary>
    public void SaveRequested(IEnumerable<string> references)
    {
        _folders.EnsureWorkFolder();
        _requested.Save(references);
    }

    /// <summary>
    /// Applies the changes in order (callers put dependencies first). Each mod goes live as soon as
    /// it is verified. If anything fails, every change in the transaction is undone.
    /// </summary>
    /// <exception cref="GameRunningException">The game is running.</exception>
    /// <exception cref="InvalidModArchiveException">An archive fails validation; nothing was changed.</exception>
    /// <exception cref="InstallException">Something failed; all changes were rolled back.</exception>
    public TransactionResult Apply(IReadOnlyList<ModChange> changes, IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (_isGameRunning())
            throw new GameRunningException();

        _folders.EnsureWorkFolder();
        using var _ = AcquireLock();
        if (_journal.Exists)
            RecoverLocked();

        var installed = _records.Load();
        var archives = new List<ModArchive>();
        try
        {
            // Open and validate everything before touching anything.
            var plan = Plan(changes, installed, archives);
            CheckFreeSpace(archives);

            var txn = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..23];
            Directory.CreateDirectory(_folders.Transaction(txn));
            var journal = new Journal(txn, DateTimeOffset.UtcNow, false, plan.Select(p => p.Step).ToList());
            _journal.Save(journal);
            _checkpoint("journal-created");

            try
            {
                for (var i = 0; i < plan.Count; i++)
                {
                    var (step, archive) = plan[i];
                    if (archive is not null)
                    {
                        var files = Extract(archive, _folders.NewStaged(txn, step.Reference), step.Reference, progress, ct);
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

                Finish(txn);
                return new TransactionResult(
                    journal.Steps.Where(s => s.NewRecord is not null).Select(s => s.NewRecord!).ToList(),
                    journal.Steps.Where(s => s.NewLocation is null).Select(s => s.Reference).ToList());
            }
            catch (Exception e) when (e is not SimulatedCrashException)
            {
                RollBack(journal, installed);
                throw e is OperationCanceledException
                    ? new InstallException("Cancelled; no mods were changed.", e)
                    : new InstallException($"Install failed and every change was undone: {e.Message}", e);
            }
        }
        finally
        {
            foreach (var archive in archives)
                archive.Dispose();
        }
    }

    /// <summary>
    /// Finishes or undoes a transaction a crash interrupted. Run on launch, before anything else
    /// touches the game's mods.
    /// </summary>
    public RecoveryResult Recover()
    {
        // Overlord has never changed this game: nothing to recover, and nothing to create on launch.
        if (!Directory.Exists(_folders.Work))
            return RecoveryResult.Clean;

        _folders.EnsureWorkFolder();
        using var _ = AcquireLock();
        return RecoverLocked();
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
            var folder = _folders.Absolute(mod.Location);
            if (!Directory.Exists(folder))
            {
                results.Add(new ModCheck(mod.Reference, ModHealth.Missing, [$"{mod.Location} is missing."]));
                continue;
            }

            var problems = new List<string>();
            foreach (var file in mod.Files)
            {
                var info = new FileInfo(Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!info.Exists)
                    problems.Add($"{file.Path} is missing.");
                else if (info.Length != file.Size)
                    problems.Add($"{file.Path} has the wrong size.");
                else if ((full || info.LastWriteTimeUtc != file.LastWriteUtc) && HashFile(info.FullName) != file.Sha256)
                    problems.Add($"{file.Path} has changed.");
            }
            results.Add(new ModCheck(mod.Reference, problems.Count == 0 ? ModHealth.Ok : ModHealth.Damaged, problems));
        }

        var known = new HashSet<string>(installed.Values.Select(m => m.Location), StringComparer.OrdinalIgnoreCase);
        var unmanaged = new List<string>();
        foreach (var (folder, prefix) in new[] { (_folders.Mods, "Mods/"), (_folders.GameFeatures, "Mods/GameFeatures/") })
        {
            if (!Directory.Exists(folder))
                continue;
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                var location = prefix + Path.GetFileName(dir);
                if (location != "Mods/GameFeatures" && !known.Contains(location))
                    unmanaged.Add(location);
            }
        }
        unmanaged.Sort(StringComparer.OrdinalIgnoreCase);

        return new CheckResult(results, unmanaged);
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

                    // Never replace a folder Overlord did not put there (another tool's or a manual install).
                    foreach (var candidate in new[] { location, GameModFolders.LocationFor(reference, !archive.Descriptor.GameFeature) })
                    {
                        if (candidate != old?.Location && Directory.Exists(_folders.Absolute(candidate)))
                            throw new InstallException($"{candidate} already exists and was not installed by Overlord.");
                    }

                    plan.Add(new PlannedStep(new JournalStep(reference, old?.Location, location, StepState.Planned, old, null), archive));
                    break;
                }
                case ModChange.Uninstall uninstall:
                {
                    var old = installed.GetValueOrDefault(uninstall.Reference)
                              ?? throw new InstallException($"{uninstall.Reference} was not installed by Overlord, so it is left alone.");
                    plan.Add(new PlannedStep(new JournalStep(old.Reference, old.Location, null, StepState.Planned, old, null), null));
                    break;
                }
            }

            if (!seen.Add(plan[^1].Step.Reference))
                throw new InstallException($"{plan[^1].Step.Reference} appears more than once in one transaction.");
        }
        return plan;
    }

    /// <summary>Extracts into the hidden folder, verifying every file as it lands.</summary>
    private List<InstalledFile> Extract(ModArchive archive, string destination, string reference,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (Directory.Exists(destination))
            DeleteTree(destination);
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

        var files = new List<InstalledFile>(archive.Entries.Count);
        for (var i = 0; i < archive.Entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = archive.Entries[i];
            var target = Path.GetFullPath(Path.Combine(destination, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidModArchiveException($"Unsafe path in archive: '{entry.RelativePath}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Hash what we write as we write it, flush it, then read it back from disk and compare.
            string expected;
            long written = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using (var source = entry.Zip.Open())
                using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = source.Read(buffer)) > 0)
                    {
                        // The archive's declared sizes are what the free-space and size checks used;
                        // an entry that inflates past its own is a zip bomb or corrupt, so stop at once.
                        if (written + read > entry.Size)
                            throw new InvalidModArchiveException($"{entry.RelativePath} extracts to more than its declared {entry.Size:N0} bytes.");
                        hash.AppendData(buffer, 0, read);
                        file.Write(buffer, 0, read);
                        written += read;
                    }
                    file.Flush(flushToDisk: true);
                }
                expected = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (written != entry.Size)
                throw new InstallException($"{reference}: {entry.RelativePath} extracted {written:N0} of {entry.Size:N0} bytes.");
            if (HashFile(target) != expected)
                throw new InstallException($"{reference}: {entry.RelativePath} did not read back the same as it was written.");

            var info = new FileInfo(target);
            files.Add(new InstalledFile(entry.RelativePath, info.Length, expected, info.LastWriteTimeUtc));
            progress?.Report(new InstallProgress(reference, InstallStage.Extracting, i + 1, archive.Entries.Count));
            _checkpoint($"extracted:{reference}:{i}");
        }
        return files;
    }

    /// <summary>Makes one step live. Idempotent: safe to repeat after a crash at any point inside it.</summary>
    private void Commit(string txn, JournalStep step, Dictionary<string, InstalledMod> installed)
    {
        var newStaged = _folders.NewStaged(txn, step.Reference);
        var oldStaged = _folders.OldStaged(txn, step.Reference);
        var oldLive = step.OldLocation is null ? null : _folders.Absolute(step.OldLocation);

        if (step.NewLocation is null)
        {
            // Uninstall: move the mod out of Mods; it is deleted with the transaction folder.
            if (oldLive is not null && Directory.Exists(oldLive) && !Directory.Exists(oldStaged))
                MoveFolder(oldLive, oldStaged, $"moved-out:{step.Reference}");
            installed.Remove(step.Reference);
        }
        else
        {
            var newLive = _folders.Absolute(step.NewLocation);
            if (Directory.Exists(newStaged))
            {
                if (oldLive is not null && Directory.Exists(oldLive) && !Directory.Exists(oldStaged))
                    MoveFolder(oldLive, oldStaged, $"moved-out:{step.Reference}");
                if (Directory.Exists(newLive))
                    throw new InstallException($"{step.NewLocation} appeared while installing; refusing to overwrite it.");
                Directory.CreateDirectory(Path.GetDirectoryName(newLive)!);
                MoveFolder(newStaged, newLive, $"moved-in:{step.Reference}");
            }
            else if (!Directory.Exists(newLive))
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
            var newStaged = _folders.NewStaged(txn, step.Reference);
            var oldStaged = _folders.OldStaged(txn, step.Reference);
            var trash = _folders.Trash(txn, step.Reference);

            // Take the new version out of Mods if it went live. When old and new share a location,
            // the folder there is only the new one while the old one is still parked in old/.
            if (step.NewLocation is not null && step.State != StepState.Planned)
            {
                var newLive = _folders.Absolute(step.NewLocation);
                var newIsLive = !Directory.Exists(newStaged) && !Directory.Exists(trash) && Directory.Exists(newLive)
                                && (step.OldLocation != step.NewLocation || Directory.Exists(oldStaged));
                if (newIsLive)
                    MoveFolder(newLive, trash, $"rolled-out:{step.Reference}");
            }

            // Put the old version back.
            if (step.OldLocation is not null)
            {
                var oldLive = _folders.Absolute(step.OldLocation);
                if (Directory.Exists(oldStaged) && !Directory.Exists(oldLive))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(oldLive)!);
                    MoveFolder(oldStaged, oldLive, $"restored:{step.Reference}");
                }
            }

            if (step.OldRecord is not null)
                installed[step.Reference] = step.OldRecord;
            else
                installed.Remove(step.Reference);
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
            foreach (var stray in Directory.EnumerateDirectories(_folders.Work, "txn-*"))
                DeleteTree(stray);
            return RecoveryResult.Clean;
        }

        var installed = _records.Load();
        if (journal.RollingBack)
            return RollBack(journal, installed);

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

        Finish(journal.TransactionId);
        return new RecoveryResult(true, rolledForward, discarded, []);
    }

    /// <summary>Removes the transaction's work folder (old versions, partial extractions), then the journal.</summary>
    private void Finish(string txn)
    {
        var folder = _folders.Transaction(txn);
        if (Directory.Exists(folder))
            DeleteTree(folder);
        _checkpoint("work-cleaned");
        _journal.Delete();
        _checkpoint("journal-removed");
    }

    private void MoveFolder(string from, string to, string checkpoint)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        // Same drive, so this is a rename. If Mods were a link to another drive this throws instead
        // of copying, and the transaction rolls back.
        Directory.Move(from, to);
        _checkpoint(checkpoint);
    }

    private void CheckFreeSpace(IReadOnlyList<ModArchive> archives)
    {
        try
        {
            DiskSpace.Require(_folders.FactoryGame, archives.Sum(a => a.UncompressedBytes) + FreeSpaceMargin, "these mods");
        }
        catch (InsufficientSpaceException e)
        {
            throw new InstallException(e.Message, e);
        }
    }

    /// <summary>Stops two Overlord windows changing the same game at once.</summary>
    private FileStream AcquireLock()
    {
        try
        {
            return new FileStream(_folders.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e)
        {
            throw new InstallException("Another Overlord window is changing this game's mods.", e);
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void DeleteTree(string path)
    {
        // Read-only files (common on Windows) would otherwise stop the delete.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }
}
