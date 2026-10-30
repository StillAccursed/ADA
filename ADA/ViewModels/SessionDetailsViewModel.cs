using ADA.Core.Saves;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADA.ViewModels;

/// <summary>
/// One session's page: its saves, newest first, and its blueprints, which belong to the session rather
/// than to any one save. Built from a snapshot; a re-read builds a new one and carries the ticks across.
/// </summary>
/// <param name="otherSessions">Every other listed session, sorted: where Copy to… can send blueprints.</param>
public partial class SessionDetailsViewModel(SessionItemViewModel session, IReadOnlyList<string> otherSessions) : ViewModelBase
{
    public SessionItemViewModel Session { get; } = session;

    public string Name => Session.Name;

    public IReadOnlyList<SaveItemViewModel> Saves { get; } = session.Saves.Select(s => new SaveItemViewModel(s)).ToList();

    public bool HasSaves => Saves.Count > 0;

    public IReadOnlyList<BlueprintRowViewModel> Blueprints { get; } =
        session.Blueprints.Select(b => new BlueprintRowViewModel(b)).ToList();

    public bool HasBlueprints => Blueprints.Count > 0;

    public IReadOnlyList<string> CopyTargets { get; } = otherSessions;

    /// <summary>The session chosen in the Copy drop-down; the first other session to start with.</summary>
    [ObservableProperty]
    public partial string? CopyTarget { get; set; } = otherSessions.FirstOrDefault();

    public bool CanCopy => HasBlueprints && CopyTargets.Count > 0;

    public string SavesHeader => $"Saves ({Saves.Count})";

    public string BlueprintsHeader => $"Blueprints ({Blueprints.Count})";

    /// <summary>Says blueprints belong to the session, and are shared by its saves when there are several.</summary>
    public string BlueprintsNote => Saves.Count > 1
        ? $"Blueprints belong to the session, so all {Saves.Count} saves share them."
        : "Blueprints belong to the session, not to one save.";

    public IReadOnlyList<Blueprint> SelectedBlueprints => Blueprints.Where(b => b.IsSelected).Select(b => b.Blueprint).ToList();

    /// <summary>The ticked blueprints, or every blueprint when none is ticked.</summary>
    public IReadOnlyList<Blueprint> ExportScope => SelectedBlueprints is { Count: > 0 } selected
        ? selected
        : Blueprints.Select(b => b.Blueprint).ToList();

    /// <summary>Ticks the blueprints that are ticked in <paramref name="previous"/>, matched by name; used after a re-read.</summary>
    internal void KeepSelectionFrom(SessionDetailsViewModel previous)
    {
        var ticked = previous.Blueprints.Where(b => b.IsSelected).Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Blueprints)
        {
            row.IsSelected = ticked.Contains(row.Name);
        }
    }

    [RelayCommand]
    private void SelectAll() => SetAll(true);

    [RelayCommand]
    private void SelectNone() => SetAll(false);

    private void SetAll(bool selected)
    {
        foreach (var row in Blueprints)
        {
            row.IsSelected = selected;
        }
    }
}
