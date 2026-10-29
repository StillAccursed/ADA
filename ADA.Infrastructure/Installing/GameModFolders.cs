using ADA.Infrastructure.IO;

namespace ADA.Infrastructure.Installing;

/// <summary>
/// The paths the install engine uses inside one game install, relative to <c>FactoryGame</c> (see
/// <see cref="GamePaths"/>). Locations in records and the journal use the same form, e.g.
/// <c>Mods/GameFeatures/X</c>.
/// </summary>
internal static class GameModFolders
{
    public const string MODS = "Mods";

    public const string GAME_FEATURES = MODS + "/GameFeatures";

    /// <summary>Hidden working area beside <c>Mods</c>, on the same filesystem, so moves into <c>Mods</c> are renames.</summary>
    public const string WORK = ".ada";

    public const string INSTALLED_RECORD = WORK + "/installed.json";

    /// <summary>The mods the user asked for, as opposed to those installed only as dependencies.</summary>
    public const string REQUESTED_RECORD = WORK + "/requested.json";

    public const string JOURNAL = WORK + "/journal.json";

    public const string PROFILE_ACTIVATION = WORK + "/profile-activation.json";

    public const string LOCK = WORK + "/lock";

    /// <summary>Mod configs, one <c>{ModReference}.cfg</c> each, as SML keeps them on both a game and a server.</summary>
    public const string CONFIGS = "Configs";

    /// <summary>The server configs the last push replaced, so it can be undone.</summary>
    public const string CONFIG_BACKUP = WORK + "/config-backup";

    /// <summary>Transaction folders are named with this prefix, so recovery can find strays.</summary>
    public const string TRANSACTION_PREFIX = "txn-";

    public static string Transaction(string id) => $"{WORK}/{TRANSACTION_PREFIX}{id}";

    /// <summary>New versions, extracted and verified before they go live.</summary>
    public static string NewStaged(string txn, string reference) => $"{Transaction(txn)}/new/{reference}";

    /// <summary>Versions moved out of <c>Mods</c> (updated or uninstalled), kept until the transaction ends.</summary>
    public static string OldStaged(string txn, string reference) => $"{Transaction(txn)}/old/{reference}";

    /// <summary>New versions taken back out of <c>Mods</c> during a rollback.</summary>
    public static string Trash(string txn, string reference) => $"{Transaction(txn)}/trash/{reference}";

    public static string LocationFor(string reference, bool gameFeature) =>
        gameFeature ? $"{GAME_FEATURES}/{reference}" : $"{MODS}/{reference}";

    public static void EnsureWorkFolder(IGameFiles files)
    {
        files.CreateDirectory(WORK);
        files.MarkHidden(WORK);
    }
}
