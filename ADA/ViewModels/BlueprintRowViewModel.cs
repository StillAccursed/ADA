using ADA.Core.Saves;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ADA.ViewModels;

/// <summary>One blueprint on a session's Blueprints tab, ticked to copy or export it.</summary>
public partial class BlueprintRowViewModel(Blueprint blueprint) : ViewModelBase
{
    public Blueprint Blueprint { get; } = blueprint;

    public string Name => Blueprint.Name;

    public string DetailText => $"{ByteSize.Format(Blueprint.Size)} · {SaveText.When(Blueprint.ModifiedUtc)}"
        + (Blueprint.ConfigPath is null ? " · no description file" : "");

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
