namespace ADA.Core.Backups;

/// <summary>The parts of an install a backup can hold; a backup holds any combination.</summary>
[Flags]
public enum BackupPart
{
    None = 0,

    /// <summary><c>Saved/SaveGames</c>, without its <c>blueprints</c> folder.</summary>
    Saves = 1,

    /// <summary><c>Saved/SaveGames/blueprints</c>.</summary>
    Blueprints = 2,

    /// <summary><c>Saved/Config</c>: graphics, input and other game settings.</summary>
    GameConfig = 4,

    /// <summary><c>FactoryGame/Configs</c>: mods' own settings.</summary>
    ModConfig = 8,

    /// <summary>ADA's settings, profiles and saved servers (never secrets, which are in the OS store).</summary>
    AdaConfig = 16,

    All = Saves | Blueprints | GameConfig | ModConfig | AdaConfig,
}

/// <summary>Why a backup was made.</summary>
public enum BackupTrigger
{
    Manual,
    Scheduled,
    BeforeModChange,

    /// <summary>The safety backup taken automatically before a restore.</summary>
    BeforeRestore,
}

/// <summary>The install a backup was taken from.</summary>
/// <param name="GamePath">The game folder, for the Mod config part; null when the backup didn't need it.</param>
/// <param name="UserDataPath">The user-data folder, for the save and game config parts; null when unknown.</param>
public sealed record BackupSource(string DisplayName, string? GamePath, string? UserDataPath);

/// <summary>One file in a backup.</summary>
/// <param name="Path">Its entry in the zip: the part's name, then the path inside the part, with <c>/</c> separators.</param>
/// <param name="Sha256">Lower-case hex, checked before any restore.</param>
public sealed record BackupFile(string Path, long Size, string Sha256);

/// <summary>What a backup holds, stored as <c>manifest.json</c> inside it.</summary>
/// <param name="Skipped">Files left out because they kept changing while being read, e.g. a save the game was writing.</param>
public sealed record BackupManifest(
    int FormatVersion,
    Guid Id,
    DateTime CreatedUtc,
    BackupTrigger Trigger,
    BackupPart Parts,
    BackupSource Source,
    IReadOnlyList<BackupFile> Files,
    IReadOnlyList<string> Skipped)
{
    public long TotalSize => Files.Sum(f => f.Size);
}

/// <summary>A backup in the backup folder.</summary>
/// <param name="Size">The <c>.zip</c>'s size on disk, after compression.</param>
public sealed record BackupInfo(string FilePath, BackupManifest Manifest, long Size);

/// <summary>The backup folder's contents.</summary>
/// <param name="Backups">Newest first.</param>
/// <param name="Unreadable"><c>.zip</c> files with no readable manifest.</param>
public sealed record BackupListing(IReadOnlyList<BackupInfo> Backups, IReadOnlyList<string> Unreadable);

/// <summary>How far a backup or restore has got, for a progress bar.</summary>
/// <param name="Current">The file being copied, as its path in the backup.</param>
public sealed record BackupProgress(int Done, int Total, string? Current);

/// <summary>What a restore did.</summary>
/// <param name="SafetyBackup">The backup of the replaced files taken just before; null when the parts held nothing to replace.</param>
/// <param name="AdaConfigPending">ADA config was restored and takes effect at ADA's next start.</param>
public sealed record RestoreResult(int FilesRestored, BackupInfo? SafetyBackup, bool AdaConfigPending);

/// <summary>A backup or restore can't go ahead; the message says why and is shown as-is.</summary>
public class BackupException(string message) : Exception(message);

/// <summary>The chosen parts hold no files yet, e.g. an install that has never been launched; not a failure.</summary>
public sealed class NothingToBackUpException(string message) : BackupException(message);
