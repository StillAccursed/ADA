namespace Overlord.Infrastructure.Installing;

/// <summary>
/// The folders the install engine uses inside one game install. Locations stored in records and
/// the journal are relative to <c>FactoryGame</c> with <c>/</c> separators, e.g. <c>Mods/GameFeatures/X</c>.
/// </summary>
internal sealed class GameModFolders(string gameRoot)
{
    public string FactoryGame { get; } = Path.Combine(gameRoot, "FactoryGame");

    public string Mods => Path.Combine(FactoryGame, "Mods");

    public string GameFeatures => Path.Combine(Mods, "GameFeatures");

    /// <summary>Hidden working area on the game's own drive, so moves into <c>Mods</c> are renames.</summary>
    public string Work => Path.Combine(FactoryGame, ".overlord");

    public string InstalledRecord => Path.Combine(Work, "installed.json");

    /// <summary>The mods the user asked for, as opposed to those installed only as dependencies.</summary>
    public string RequestedRecord => Path.Combine(Work, "requested.json");

    public string Journal => Path.Combine(Work, "journal.json");

    public string Lock => Path.Combine(Work, "lock");

    public string Transaction(string id) => Path.Combine(Work, "txn-" + id);

    /// <summary>New versions, extracted and verified before they go live.</summary>
    public string NewStaged(string txn, string reference) => Path.Combine(Transaction(txn), "new", reference);

    /// <summary>Versions moved out of <c>Mods</c> (updated or uninstalled), kept until the transaction ends.</summary>
    public string OldStaged(string txn, string reference) => Path.Combine(Transaction(txn), "old", reference);

    /// <summary>New versions taken back out of <c>Mods</c> during a rollback.</summary>
    public string Trash(string txn, string reference) => Path.Combine(Transaction(txn), "trash", reference);

    public static string LocationFor(string reference, bool gameFeature) =>
        gameFeature ? $"Mods/GameFeatures/{reference}" : $"Mods/{reference}";

    public string Absolute(string location) => Path.Combine(FactoryGame, location.Replace('/', Path.DirectorySeparatorChar));

    public void EnsureWorkFolder()
    {
        var info = Directory.CreateDirectory(Work);
        // The leading dot hides it on macOS and Linux; Windows needs the attribute.
        if (OperatingSystem.IsWindows() && !info.Attributes.HasFlag(FileAttributes.Hidden))
            info.Attributes |= FileAttributes.Hidden;
    }
}
