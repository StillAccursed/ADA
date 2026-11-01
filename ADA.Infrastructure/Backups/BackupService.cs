using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADA.Core.Activity;
using ADA.Core.Backups;
using ADA.Core.Games;
using ADA.Infrastructure.Activity;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Saves;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Backups;

/// <summary>
/// Creates, lists, restores and deletes backups of an install's saves, blueprints and configs, and of ADA's
/// own config. Each backup is one <c>.zip</c> with a <c>manifest.json</c>. It's written under a temporary name
/// and renamed only when complete, and every file's SHA-256 is checked before a restore touches anything.
/// One backup or restore runs at a time; others wait.
/// </summary>
/// <param name="folderProvider">Where backups are kept, read on every call, so a changed setting takes effect at once.</param>
/// <param name="adaConfigFolder">ADA's <c>Config</c> folder, the ADA config part.</param>
/// <param name="pendingAdaConfigFolder">Where restored ADA config waits for the next start; see <see cref="PendingAdaConfig"/>.</param>
/// <param name="isGameRunning">Whether the install's game is running; restores refuse while it is.</param>
/// <param name="freeSpace">Free bytes on the drive holding a path, or null when unknown; the drive's own by default.</param>
/// <param name="time">The clock backups are named and stamped with.</param>
/// <param name="activity">
/// Where backups, restores and deletes are recorded. Successful scheduled backups and pruning aren't: they're automatic
/// housekeeping, listed on the Backups page.
/// </param>
public sealed class BackupService(
    Func<string> folderProvider,
    string adaConfigFolder,
    string pendingAdaConfigFolder,
    Func<GameInstall, CancellationToken, Task<bool>> isGameRunning,
    ILogger<BackupService>? log = null,
    Func<string, long?>? freeSpace = null,
    TimeProvider? time = null,
    ActivityLog? activity = null)
{
    internal const string MANIFEST_ENTRY = "manifest.json";

    private const int FORMAT_VERSION = 1;
    private const string BACKUP_EXTENSION = ".zip";
    private const string PARTIAL_EXTENSION = ".partial";
    private const string SAVE_EXTENSION = ".sav";
    private const string TEMP_EXTENSION = ".tmp";
    private const string CRASH_REPORTS_FOLDER = "CrashReportClient";

    // Zip overhead and filesystem rounding; most parts compress, so this is generous.
    private const double SPACE_MARGIN = 1.1;

    private const int CHANGING_FILE_ATTEMPTS = 3;

    private static readonly TimeSpan CHANGING_FILE_WAIT = TimeSpan.FromMilliseconds(500);

    private static readonly BackupPart[] PARTS =
        [BackupPart.Saves, BackupPart.Blueprints, BackupPart.GameConfig, BackupPart.ModConfig, BackupPart.AdaConfig];

    private readonly ILogger _log = log ?? NullLogger<BackupService>.Instance;
    private readonly Func<string, long?> _freeSpace = freeSpace ?? (path => DiskSpace.DriveFor(path)?.AvailableFreeSpace);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Raised after a backup is written, including a restore's safety copy, on the thread that made it and while
    /// other backups and restores wait; handlers must not start a backup or restore themselves.
    /// </summary>
    public event EventHandler<BackupInfo>? BackupCreated;

    public string Folder => folderProvider();

    /// <summary>A service whose folder never changes; for tests.</summary>
    public BackupService(string folder, string adaConfigFolder, string pendingAdaConfigFolder,
        Func<GameInstall, CancellationToken, Task<bool>> isGameRunning, ILogger<BackupService>? log = null,
        Func<string, long?>? freeSpace = null, TimeProvider? time = null, ActivityLog? activity = null)
        : this(() => folder, adaConfigFolder, pendingAdaConfigFolder, isGameRunning, log, freeSpace, time, activity)
    {
    }

    /// <summary>The parts whose folders can be known for <paramref name="game"/>; see <see cref="BackupPart"/>.</summary>
    public static BackupPart AvailableParts(GameInstall game) =>
        BackupPart.AdaConfig
        | (game.UserDataPath is null ? BackupPart.None : BackupPart.Saves | BackupPart.Blueprints | BackupPart.GameConfig)
        | (game.IsRemote ? BackupPart.None : BackupPart.ModConfig);

    /// <summary>Backs up <paramref name="parts"/> of <paramref name="game"/>. Works while the game is running.</summary>
    /// <exception cref="BackupException">
    /// A chosen part isn't available for this install, no part was chosen, or the chosen parts have no files yet.
    /// </exception>
    /// <exception cref="IOException">Not enough free space, or writing failed; nothing is left behind.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    public Task<BackupInfo> CreateAsync(GameInstall game, BackupPart parts, BackupTrigger trigger,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default) =>
        activity.RecordAsync(ActivityArea.Backups, ActivityActions.BACKUP_CREATED, game,
            () => CreateCoreAsync(game, parts, trigger, progress, ct),
            backup => trigger == BackupTrigger.Scheduled ? null : new ActivityDescription(
                $"Backed up {DescribeParts(parts)} ({DiskSpace.Format(backup.Size)})",
                [Path.GetFileName(backup.FilePath), .. backup.Manifest.Skipped.Select(s => $"Skipped {s}: it kept changing")]),
            trigger == BackupTrigger.Scheduled ? "Scheduled backup failed" : "Backup failed",
            isNotAFailure: e => e is NothingToBackUpException && trigger != BackupTrigger.Manual);

    private async Task<BackupInfo> CreateCoreAsync(GameInstall game, BackupPart parts, BackupTrigger trigger,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return (await Task.Run(() => Create(game, parts, trigger, progress, ct, refuseEmpty: true), ct))!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every backup in the folder, newest first, reading only their manifests.</summary>
    public Task<BackupListing> ListAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var folder = Folder;
        if (!Directory.Exists(folder))
        {
            return new BackupListing([], []);
        }

        var backups = new List<BackupInfo>();
        var unreadable = new List<string>();
        foreach (var file in Directory.GetFiles(folder, "*" + BACKUP_EXTENSION))
        {
            ct.ThrowIfCancellationRequested();
            if (TryRead(file) is { } info)
            {
                backups.Add(info);
            }
            else
            {
                unreadable.Add(file);
            }
        }
        return new BackupListing(backups.OrderByDescending(b => b.Manifest.CreatedUtc).ToList(), unreadable.Order(StringComparer.Ordinal).ToList());
    }, ct);

    /// <summary>
    /// Restores <paramref name="parts"/> of <paramref name="backup"/> into <paramref name="target"/>. In order: refuses
    /// while the game runs; checks every chosen file's hash and path, touching nothing if one is wrong; takes a
    /// safety backup of the same parts (none when they hold no files yet, as there's nothing to replace); then writes
    /// each file over the current one in one step. Files not in the backup are left alone. ADA config is staged for
    /// the next start rather than written over live files.
    /// </summary>
    /// <exception cref="BackupException">The game is running, the backup is damaged or unsafe, or a part can't be restored here.</exception>
    /// <exception cref="IOException">Writing failed after the safety backup; files restored so far stay restored.</exception>
    public Task<RestoreResult> RestoreAsync(BackupInfo backup, GameInstall target, BackupPart parts,
        IProgress<BackupProgress>? progress = null, CancellationToken ct = default) =>
        activity.RecordAsync(ActivityArea.Backups, ActivityActions.BACKUP_RESTORED, target,
            () => RestoreCoreAsync(backup, target, parts, progress, ct),
            result => new ActivityDescription(
                $"Restored {DescribeParts(parts)} ({ActivityText.Count(result.FilesRestored, "file")})",
                [
                    $"From {Path.GetFileName(backup.FilePath)}",
                    result.SafetyBackup is { } safety ? $"Safety copy: {Path.GetFileName(safety.FilePath)}" : "No safety copy needed",
                    .. result.AdaConfigPending ? ["ADA settings apply at the next start"] : Array.Empty<string>(),
                ]),
            "Restore failed", [$"From {Path.GetFileName(backup.FilePath)}"]);

    private async Task<RestoreResult> RestoreCoreAsync(BackupInfo backup, GameInstall target, BackupPart parts,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        if (parts == BackupPart.None || (parts & ~backup.Manifest.Parts) != BackupPart.None)
        {
            throw new BackupException("Choose parts that this backup holds.");
        }
        foreach (var part in Split(parts))
        {
            if (RootFor(part, target) is null)
            {
                throw new BackupException($"{Describe(part)} can't be restored to {target.DisplayName}: ADA doesn't know where it keeps them.");
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (await isGameRunning(target, ct))
            {
                throw new BackupException("Close Satisfactory before restoring: it may have these files open, and would overwrite them.");
            }

            var files = backup.Manifest.Files.Where(f => parts.HasFlag(PartOf(f.Path))).ToList();
            await Task.Run(() => Verify(backup.FilePath, files, ct), ct);
            var safety = await Task.Run(() => Create(target, parts, BackupTrigger.BeforeRestore, null, ct, refuseEmpty: false), ct);
            var restored = await Task.Run(() => Extract(backup.FilePath, files, target, progress, ct), ct);
            _log.LogInformation("Restored {Count} files ({Parts}) from {Backup} to {Game}; safety backup {Safety}",
                restored, parts, backup.FilePath, target.DisplayName, safety?.FilePath ?? "not needed");
            return new RestoreResult(restored, safety, parts.HasFlag(BackupPart.AdaConfig));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Deletes backups of <paramref name="trigger"/> from the same install as <paramref name="source"/>, keeping the
    /// newest <paramref name="keep"/>. Hand-made backups are never pruned.
    /// </summary>
    /// <returns>How many were deleted.</returns>
    /// <exception cref="ArgumentException"><paramref name="trigger"/> is <see cref="BackupTrigger.Manual"/>.</exception>
    public async Task<int> PruneAsync(BackupTrigger trigger, BackupSource source, int keep, CancellationToken ct = default)
    {
        if (trigger == BackupTrigger.Manual)
        {
            throw new ArgumentException("Hand-made backups are never pruned.", nameof(trigger));
        }

        var listing = await ListAsync(ct);
        var old = listing.Backups
            .Where(b => b.Manifest.Trigger == trigger && IsSameInstall(b.Manifest.Source, source))
            .Skip(Math.Max(keep, 0))
            .ToList();
        foreach (var backup in old)
        {
            await DeleteFileAsync(backup);
        }
        return old.Count;
    }

    /// <summary>Deletes a backup the user chose; pruning deletes through <see cref="DeleteFileAsync"/>, which records nothing.</summary>
    public Task DeleteAsync(BackupInfo backup) =>
        activity.RecordAsync(ActivityArea.Backups, ActivityActions.BACKUP_DELETED, null, () => DeleteFileAsync(backup),
            new ActivityDescription($"Deleted the backup from {backup.Manifest.CreatedUtc.ToLocalTime():g}",
                [Path.GetFileName(backup.FilePath)]),
            "Deleting a backup failed", [Path.GetFileName(backup.FilePath)]);

    /// <summary>"Everything", or the parts' names joined, e.g. "Saves, Mod settings".</summary>
    public static string DescribeParts(BackupPart parts) => parts == BackupPart.All
        ? "Everything"
        : string.Join(", ", Split(parts).Select(Describe));

    private Task DeleteFileAsync(BackupInfo backup) => Task.Run(() =>
    {
        File.Delete(backup.FilePath);
        _log.LogInformation("Deleted backup {Backup}", backup.FilePath);
    });

    /// <summary>Plain-language name of a part, as the Backups page shows it.</summary>
    public static string Describe(BackupPart part) => part switch
    {
        BackupPart.Saves => "Saves",
        BackupPart.Blueprints => "Blueprints",
        BackupPart.GameConfig => "Game settings",
        BackupPart.ModConfig => "Mod settings",
        BackupPart.AdaConfig => "ADA settings",
        _ => part.ToString(),
    };

    /// <summary>
    /// Whether two backups came from the same install: the same user-data folder, and the same game folder where
    /// both recorded one. A backup records the game folder only when it holds mod settings.
    /// </summary>
    public static bool IsSameInstall(BackupSource a, BackupSource b) =>
        string.Equals(a.UserDataPath, b.UserDataPath, StringComparison.Ordinal)
        && (a.GamePath is null || b.GamePath is null || string.Equals(a.GamePath, b.GamePath, StringComparison.Ordinal));

    /// <summary>The single parts in <paramref name="parts"/>, in a fixed order.</summary>
    public static IEnumerable<BackupPart> Split(BackupPart parts) => PARTS.Where(p => parts.HasFlag(p));

    /// <param name="refuseEmpty">
    /// True throws when the parts hold no files; false returns null instead, for a safety backup of parts that
    /// have nothing to replace yet.
    /// </param>
    private BackupInfo? Create(GameInstall game, BackupPart parts, BackupTrigger trigger, IProgress<BackupProgress>? progress,
        CancellationToken ct, bool refuseEmpty)
    {
        var unavailable = parts & ~AvailableParts(game);
        if (parts == BackupPart.None || unavailable != BackupPart.None)
        {
            throw new BackupException(parts == BackupPart.None
                ? "Choose at least one part to back up."
                : $"{string.Join(", ", Split(unavailable).Select(Describe))} can't be backed up for {game.DisplayName}.");
        }

        var sources = Split(parts).SelectMany(part => Sources(part, RootFor(part, game)!)).ToList();
        if (sources.Count == 0)
        {
            return refuseEmpty
                ? throw new NothingToBackUpException("There's nothing to back up yet: the chosen parts' folders are empty or don't exist.")
                : null;
        }
        var folder = Folder;
        Directory.CreateDirectory(folder);
        RemoveLeftovers(folder);
        var needed = (long)(sources.Sum(s => s.Size) * SPACE_MARGIN);
        if (_freeSpace(folder) is { } free && free < needed)
        {
            throw new IOException($"Not enough free space for the backup: it needs {DiskSpace.Format(needed)}, and {DiskSpace.Format(free)} is free.");
        }

        var created = _time.GetUtcNow().UtcDateTime;
        var final = FreeFileName(folder, created, trigger);
        var partial = final + PARTIAL_EXTENSION;
        var files = new List<BackupFile>();
        var skipped = new List<string>();
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (var i = 0; i < sources.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var source = sources[i];
                        progress?.Report(new BackupProgress(i, sources.Count, source.EntryPath));
                        if (ReadSettled(source.FullPath, ct) is not { } bytes)
                        {
                            skipped.Add(source.EntryPath);
                            continue;
                        }
                        var level = Path.GetExtension(source.FullPath).Equals(SAVE_EXTENSION, StringComparison.OrdinalIgnoreCase)
                            ? CompressionLevel.NoCompression
                            : CompressionLevel.Optimal;
                        using (var entry = zip.CreateEntry(source.EntryPath, level).Open())
                        {
                            entry.Write(bytes);
                        }
                        files.Add(new BackupFile(source.EntryPath, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
                    }

                    var manifest = new BackupManifest(FORMAT_VERSION, Guid.NewGuid(), created, trigger, parts,
                        new BackupSource(game.DisplayName, parts.HasFlag(BackupPart.ModConfig) ? game.GamePath : null, game.UserDataPath),
                        files, skipped);
                    using (var entry = zip.CreateEntry(MANIFEST_ENTRY, CompressionLevel.Optimal).Open())
                    {
                        JsonSerializer.Serialize(entry, manifest, BackupJsonContext.Default.BackupManifest);
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(partial, final);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        progress?.Report(new BackupProgress(sources.Count, sources.Count, null));
        _log.LogInformation("Backed up {Count} files ({Parts}, {Trigger}) of {Game} to {Backup}; {Skipped} skipped",
            files.Count, parts, trigger, game.DisplayName, final, skipped.Count);
        var info = TryRead(final) ?? throw new IOException($"The backup {final} was written but can't be read back.");
        BackupCreated?.Invoke(this, info);
        return info;
    }

    private sealed record Source(string EntryPath, string FullPath, long Size);

    /// <summary>Every file in a part's folder, minus what the part leaves out, temporary files and links.</summary>
    private IEnumerable<Source> Sources(BackupPart part, string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (LeftOut(part, relative) || Path.GetExtension(file).Equals(TEMP_EXTENSION, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            yield return new Source($"{part}/{relative}", file, new FileInfo(file).Length);
        }
    }

    private static bool LeftOut(BackupPart part, string relative) => part switch
    {
        BackupPart.Saves => relative.StartsWith(SaveLibrary.BLUEPRINTS_FOLDER + "/", StringComparison.OrdinalIgnoreCase),
        BackupPart.GameConfig => relative.StartsWith(CRASH_REPORTS_FOLDER + "/", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// Reads a file that the game may be writing: it counts only if its size and time are the same before and
    /// after the read. Retries a few times; null when it never settles or has disappeared.
    /// </summary>
    private byte[]? ReadSettled(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < CHANGING_FILE_ATTEMPTS; attempt++)
        {
            if (attempt > 0)
            {
                Task.Delay(CHANGING_FILE_WAIT, _time, ct).GetAwaiter().GetResult();
            }

            var before = new FileInfo(path);
            if (!before.Exists)
            {
                return null;
            }
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            var after = new FileInfo(path);
            if (after.Exists && after.Length == before.Length && after.LastWriteTimeUtc == before.LastWriteTimeUtc
                && bytes.Length == before.Length)
            {
                return bytes;
            }
        }
        _log.LogWarning("Skipped {File} in a backup: it kept changing while being read", path);
        return null;
    }

    /// <exception cref="BackupException">A file is missing, damaged, or has an unsafe path.</exception>
    private static void Verify(string backup, IReadOnlyList<BackupFile> files, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(backup);
        var bad = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsSafeEntry(file.Path) || zip.GetEntry(file.Path) is not { } entry)
            {
                bad.Add(file.Path);
                continue;
            }
            using var stream = entry.Open();
            if (!Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.Ordinal))
            {
                bad.Add(file.Path);
            }
        }
        if (bad.Count > 0)
        {
            throw new BackupException($"This backup is damaged, so nothing was restored. {bad.Count} file(s) don't match: {string.Join(", ", bad.Take(5))}");
        }
    }

    private int Extract(string backup, IReadOnlyList<BackupFile> files, GameInstall target, IProgress<BackupProgress>? progress,
        CancellationToken ct)
    {
        if (files.Any(f => PartOf(f.Path) == BackupPart.AdaConfig) && Directory.Exists(pendingAdaConfigFolder))
        {
            // A restore that was never applied is replaced by this one.
            Directory.Delete(pendingAdaConfigFolder, recursive: true);
        }

        using var zip = ZipFile.OpenRead(backup);
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            progress?.Report(new BackupProgress(i, files.Count, file.Path));
            var part = PartOf(file.Path);
            var root = part == BackupPart.AdaConfig ? pendingAdaConfigFolder : RootFor(part, target)!;
            var relative = file.Path[(file.Path.IndexOf('/') + 1)..];
            var destination = Path.Combine([root, .. relative.Split('/')]);
            AtomicFile.Write(destination, stream =>
            {
                using var entry = zip.GetEntry(file.Path)!.Open();
                entry.CopyTo(stream);
            });
        }
        progress?.Report(new BackupProgress(files.Count, files.Count, null));
        return files.Count;
    }

    /// <returns>The part's folder for <paramref name="game"/>, or null when it can't be known.</returns>
    private string? RootFor(BackupPart part, GameInstall game) => part switch
    {
        BackupPart.Saves => game.UserDataPath is { } user ? SaveLibrary.SaveGamesPath(user) : null,
        BackupPart.Blueprints => game.UserDataPath is { } user ? Path.Combine(SaveLibrary.SaveGamesPath(user), SaveLibrary.BLUEPRINTS_FOLDER) : null,
        BackupPart.GameConfig => game.UserDataPath is { } user ? Path.Combine(user, "Saved", "Config") : null,
        BackupPart.ModConfig => game.IsRemote ? null : Path.Combine(game.GamePath, "FactoryGame", "Configs"),
        BackupPart.AdaConfig => adaConfigFolder,
        _ => null,
    };

    /// <summary>The part a file belongs to, from its entry path's first segment; <see cref="BackupPart.None"/> when unknown.</summary>
    private static BackupPart PartOf(string entryPath)
    {
        var slash = entryPath.IndexOf('/');
        return slash > 0 && Enum.TryParse<BackupPart>(entryPath[..slash], out var part) && PARTS.Contains(part)
            ? part
            : BackupPart.None;
    }

    /// <summary>A known part, then a path that stays inside it, by the installer's own rule.</summary>
    private static bool IsSafeEntry(string entryPath) =>
        PartOf(entryPath) != BackupPart.None && GamePaths.IsSafe(entryPath[(entryPath.IndexOf('/') + 1)..])
        && entryPath[(entryPath.IndexOf('/') + 1)..].Length > 0;

    private static string FreeFileName(string folder, DateTime created, BackupTrigger trigger)
    {
        var name = $"ADA {created.ToLocalTime():yyyy-MM-dd HHmmss} {trigger}";
        var path = Path.Combine(folder, name + BACKUP_EXTENSION);
        for (var n = 2; File.Exists(path) || File.Exists(path + PARTIAL_EXTENSION); n++)
        {
            path = Path.Combine(folder, $"{name} ({n}){BACKUP_EXTENSION}");
        }
        return path;
    }

    // Only called under the gate, so no backup is being written at the time.
    private void RemoveLeftovers(string folder)
    {
        foreach (var partial in Directory.GetFiles(folder, "*" + PARTIAL_EXTENSION))
        {
            File.Delete(partial);
            _log.LogInformation("Removed an unfinished backup, {File}", partial);
        }
    }

    private static BackupInfo? TryRead(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            if (zip.GetEntry(MANIFEST_ENTRY) is not { } entry)
            {
                return null;
            }
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize(stream, BackupJsonContext.Default.BackupManifest);
            return manifest is null ? null : new BackupInfo(file, manifest, new FileInfo(file).Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
