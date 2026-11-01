namespace ADA.Core.Activity;

/// <summary>Which part of ADA an activity belongs to; the Activity page filters by it.</summary>
public enum ActivityArea
{
    Mods,
    Profiles,
    Backups,
    Saves,
    Servers,
    Game,
}

public enum ActivityOutcome
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// The kinds of activity, stored as text so an older ADA can still read entries a newer one records. Step 2 records
/// each of these; the page shows <see cref="ActivityEntry.Summary"/>, not these names.
/// </summary>
public static class ActivityActions
{
    public const string MODS_CHANGED = "ModsChanged";
    public const string MODS_REPAIRED = "ModsRepaired";
    public const string MODS_RECOVERED = "ModsRecovered";
    public const string MODS_TAKEN_OVER = "ModsTakenOver";
    public const string PROFILE_SWITCHED = "ProfileSwitched";
    public const string PROFILE_CREATED = "ProfileCreated";
    public const string PROFILE_IMPORTED = "ProfileImported";
    public const string PROFILE_DELETED = "ProfileDeleted";
    public const string PROFILE_EXPORTED = "ProfileExported";
    public const string BACKUP_CREATED = "BackupCreated";
    public const string BACKUP_RESTORED = "BackupRestored";
    public const string BACKUP_DELETED = "BackupDeleted";
    public const string BLUEPRINTS_COPIED = "BlueprintsCopied";
    public const string BLUEPRINTS_IMPORTED = "BlueprintsImported";
    public const string BLUEPRINTS_EXPORTED = "BlueprintsExported";
    public const string SERVER_CONNECTED = "ServerConnected";
    public const string SERVER_DISCONNECTED = "ServerDisconnected";
    public const string SERVER_LOCK_TAKEN_OVER = "ServerLockTakenOver";
    public const string CONFIGS_PUSHED = "ConfigsPushed";
    public const string CONFIGS_RESTORED = "ConfigsRestored";
    public const string GAME_PLAYED = "GamePlayed";
}

/// <summary>One thing ADA did, as the Activity page shows it.</summary>
/// <param name="AtUtc">When it finished (or failed).</param>
/// <param name="Action">One of <see cref="ActivityActions"/>, or an action from a newer ADA.</param>
/// <param name="Summary">One line for the list, e.g. "Installed 3 mods, updated 1".</param>
/// <param name="Install">The install's display name when it happened; null for activity not about one install.</param>
/// <param name="InstallKey">The install's key (<c>GameInstall.GamePath</c>), for filtering; null with <paramref name="Install"/>.</param>
/// <param name="Details">Lines shown when the entry is expanded, e.g. <c>SML 3.11.0 → 3.12.0</c>.</param>
/// <param name="Error">What the user was told when it failed; null otherwise.</param>
public sealed record ActivityEntry(
    Guid Id,
    DateTime AtUtc,
    ActivityArea Area,
    string Action,
    ActivityOutcome Outcome,
    string Summary,
    string? Install,
    string? InstallKey,
    IReadOnlyList<string> Details,
    string? Error);
