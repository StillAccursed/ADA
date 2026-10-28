using CommunityToolkit.Mvvm.ComponentModel;

namespace ADA.ViewModels;

/// <summary>One tag in the Mods page's tag list.</summary>
public partial class TagOptionViewModel(string name, int count) : ViewModelBase
{
    public string Name { get; } = name;

    public int Count { get; } = count;

    public string Label => $"{Name} ({Count:N0})";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
