namespace ADA.Core.Saves;

/// <summary>One world save the game can load, with what its header says.</summary>
/// <param name="FilePath">Host path of the <c>.sav</c>.</param>
/// <param name="AccountFolder">
/// The <c>SaveGames</c> subfolder it's in: the platform account id, or <c>server</c> for a dedicated server.
/// </param>
/// <param name="ModifiedUtc">When the file last changed; later than the header's save time when the file was copied.</param>
public sealed record SaveGame(string FilePath, string AccountFolder, long Size, DateTime ModifiedUtc, SaveHeader Header)
{
    /// <summary>The name the game's load menu shows; the file name for saves from before 1.0.</summary>
    public string Name => Header.SaveName ?? Path.GetFileNameWithoutExtension(FilePath);
}

/// <summary>A blueprint: the <c>.sbp</c> the game builds from, and the <c>.sbpcfg</c> holding its description and icon.</summary>
/// <param name="Session">The session folder it's in, under <c>SaveGames/blueprints</c>.</param>
/// <param name="ConfigPath">The <c>.sbpcfg</c>, or null when there's none beside the <c>.sbp</c>.</param>
/// <param name="Size">Both files together.</param>
/// <param name="ModifiedUtc">The later of the two files' times.</param>
public sealed record Blueprint(
    string Name,
    string Session,
    string BlueprintPath,
    string? ConfigPath,
    long Size,
    DateTime ModifiedUtc);

/// <summary>Everything listed under one install's user-data folder.</summary>
/// <param name="Saves">Readable saves, newest save time first.</param>
/// <param name="UnreadableSaves">
/// <c>.sav</c> files in account folders whose header couldn't be read: corrupt, being written, or from
/// before Update 5. Sorted by path.
/// </param>
/// <param name="Blueprints">Every blueprint, by session then name.</param>
public sealed record SaveFolderContents(
    string UserDataPath,
    IReadOnlyList<SaveGame> Saves,
    IReadOnlyList<string> UnreadableSaves,
    IReadOnlyList<Blueprint> Blueprints)
{
    public static SaveFolderContents Empty(string userDataPath) => new(userDataPath, [], [], []);

    /// <summary>
    /// Every session with saves or blueprints, sorted, each once regardless of case; a session with only
    /// blueprints is included, so blueprints can still be copied to it.
    /// </summary>
    public IReadOnlyList<string> Sessions =>
        Saves.Select(s => s.Header.SessionName)
            .Concat(Blueprints.Select(b => b.Session))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
