using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Overlord.Core.Catalog;
using Overlord.Infrastructure.Catalog;

namespace Overlord.ViewModels;

/// <summary>One mod in the Mods page list, and the details shown when it is selected.</summary>
public partial class ModItemViewModel(CatalogMod mod) : ViewModelBase
{
    private string? _description;

    public CatalogMod Mod { get; } = mod;

    public string Reference => Mod.Reference;

    public string Name => Mod.Name;

    public string ShortDescription => Mod.ShortDescription;

    public string AuthorsText => Mod.Authors.Count == 0 ? "" : "by " + string.Join(", ", Mod.Authors);

    public string DownloadsText => $"{Mod.Downloads.ToString("N0", CultureInfo.CurrentCulture)} downloads";

    public string TagsText => string.Join(" · ", Mod.Tags);

    public bool HasTags => Mod.Tags.Count > 0;

    // Built on first selection: descriptions can be long, and most listed mods are never opened.
    public string Description => _description ??= ModDescription.Readable(Mod.FullDescription) is { Length: > 0 } text
        ? text
        : Mod.ShortDescription;

    public string? CompatibilityText => Mod.CompatibilityRelease switch
    {
        CompatibilityState.Damaged => "Reported partly broken on the current game version",
        CompatibilityState.Broken => "Reported broken on the current game version",
        _ => null,
    };

    /// <summary>The version installed in the selected game, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(InstalledText), nameof(CanBeRequested))]
    public partial string? InstalledVersion { get; set; }

    /// <summary>Installed only because another mod needs it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText), nameof(CanBeRequested))]
    public partial bool IsDependencyOnly { get; set; }

    public bool IsInstalled => InstalledVersion is not null;

    /// <summary>Install is offered: not installed, or only there as a dependency (installing keeps it).</summary>
    public bool CanBeRequested => !IsInstalled || IsDependencyOnly;

    public string? InstalledText => InstalledVersion switch
    {
        null => null,
        var v when IsDependencyOnly => $"Installed {v} · dependency",
        var v => $"Installed {v}",
    };

    [ObservableProperty]
    public partial Bitmap? Logo { get; set; }

    /// <summary>The newest version for the selected game, once looked up.</summary>
    [ObservableProperty]
    public partial string? LatestText { get; set; }
}
