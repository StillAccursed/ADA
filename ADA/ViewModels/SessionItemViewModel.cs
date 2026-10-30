using ADA.Core.Saves;

namespace ADA.ViewModels;

/// <summary>
/// One session (playthrough) on the Saves page: its saves, newest first, and its blueprints. A session with
/// only blueprints is still listed, so blueprints can be copied into it. Immutable: a re-read builds new items.
/// </summary>
public sealed class SessionItemViewModel(string name, IReadOnlyList<SaveGame> saves, IReadOnlyList<Blueprint> blueprints)
{
    public string Name { get; } = name;

    /// <summary>Newest save time first.</summary>
    public IReadOnlyList<SaveGame> Saves { get; } = saves;

    public IReadOnlyList<Blueprint> Blueprints { get; } = blueprints;

    public DateTime? LastSavedUtc => Saves.Count == 0 ? null : Saves[0].Header.SavedAtUtc;

    /// <summary>The longest playtime among the saves: the session's furthest point.</summary>
    public TimeSpan PlayTime => Saves.Count == 0 ? TimeSpan.Zero : Saves.Max(s => s.Header.PlayTime);

    public string PlayTimeText => Saves.Count == 0 ? "—" : SaveText.PlayTime(PlayTime);

    public string LastSavedText => LastSavedUtc is { } saved ? SaveText.When(saved) : "No saves";

    public string SavesText => Saves.Count switch
    {
        0 => "No saves",
        1 => "1 save",
        var count => $"{count} saves",
    };

    public string BlueprintsText => Blueprints.Count switch
    {
        0 => "No blueprints",
        1 => "1 blueprint",
        var count => $"{count} blueprints",
    };

    /// <summary>The newest save's mod count; a session's saves usually share their mods.</summary>
    public string ModsText => Saves.Count == 0 ? "" : new SaveItemViewModel(Saves[0]).ModsText;

    /// <summary>Whether the session name or any of its save names contains <paramref name="search"/>, ignoring case.</summary>
    internal bool Matches(string search) =>
        Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || Saves.Any(s => s.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// The sessions in a folder's contents: saves grouped by session name and blueprint folders matched to them,
    /// ignoring case. The name's casing comes from the newest save, or the blueprint folder when there's no save.
    /// </summary>
    internal static IReadOnlyList<SessionItemViewModel> From(SaveFolderContents contents)
    {
        var saves = contents.Saves
            .GroupBy(s => s.Header.SessionName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SaveGame>)g.ToList(), StringComparer.OrdinalIgnoreCase);
        var blueprints = contents.Blueprints
            .GroupBy(b => b.Session, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Blueprint>)g.ToList(), StringComparer.OrdinalIgnoreCase);
        return saves.Keys.Concat(blueprints.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new SessionItemViewModel(
                saves.GetValueOrDefault(name)?[0].Header.SessionName ?? name,
                saves.GetValueOrDefault(name) ?? [],
                blueprints.GetValueOrDefault(name) ?? []))
            .ToList();
    }
}
