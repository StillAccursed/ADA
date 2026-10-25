using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overlord.Core.Catalog;
using Overlord.Infrastructure.Catalog;

namespace Overlord.ViewModels;

/// <summary>One mod in the Mods page list, and the details shown when it is selected.</summary>
/// <param name="setFavourite">Saves a star; null where starring isn't offered.</param>
public partial class ModItemViewModel(CatalogMod mod, Action<ModItemViewModel, bool>? setFavourite = null) : ViewModelBase
{
    private string? _description;

    public CatalogMod Mod { get; } = mod;

    public string Reference => Mod.Reference;

    public string Name => Mod.Name;

    public string ShortDescription => Mod.ShortDescription;

    public string AuthorsText => Mod.Authors.Count == 0 ? "" : "by " + string.Join(", ", Mod.Authors);

    public string DownloadsText => $"{Mod.Downloads.ToString("N0", CultureInfo.CurrentCulture)} downloads";

    public string ViewsText => $"{Mod.Views.ToString("N0", CultureInfo.CurrentCulture)} views";

    public string UpdatedText => Mod.LastVersionDate is { } at
        ? $"Updated {at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}"
        : "";

    /// <summary>Starred by the user; saved for every game.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavouriteGlyph))]
    public partial bool IsFavourite { get; set; }

    public string FavouriteGlyph => IsFavourite ? "★" : "☆";

    /// <summary>Waiting in the selected game's install queue.</summary>
    [ObservableProperty]
    public partial bool IsQueued { get; set; }

    [RelayCommand]
    private void ToggleFavourite()
    {
        IsFavourite = !IsFavourite;
        setFavourite?.Invoke(this, IsFavourite);
    }

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
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(InstalledText), nameof(CanBeRequested), nameof(CanInstallHere))]
    public partial string? InstalledVersion { get; set; }

    /// <summary>Installed only because another mod needs it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText), nameof(CanBeRequested), nameof(CanInstallHere))]
    public partial bool IsDependencyOnly { get; set; }

    public bool IsInstalled => InstalledVersion is not null;

    /// <summary>Install is offered: not installed, or only there as a dependency (installing keeps it).</summary>
    public bool CanBeRequested => !IsInstalled || IsDependencyOnly;

    /// <summary>Install is offered on the mod page: as on the list, or to switch to a picked version.</summary>
    public bool CanInstallHere => CanBeRequested || CanSwitchVersion;

    public string? InstalledText => InstalledVersion switch
    {
        null => null,
        var v when IsDependencyOnly => $"Installed {v} · dependency",
        var v => $"Installed {v}",
    };

    [ObservableProperty]
    public partial Bitmap? Logo { get; set; }

    // ---- The mod page, filled in when the mod is opened.

    /// <summary>The mod's page on ficsit.app; the reference is letters, digits and _ only.</summary>
    public Uri FicsitUri => new($"https://ficsit.app/mod/{Reference}");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersions))]
    public partial IReadOnlyList<VersionRowViewModel> Versions { get; set; } = [];

    public bool HasVersions => Versions.Count > 0;

    /// <summary>What the newest compatible version needs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDependencies))]
    public partial IReadOnlyList<DependencyRowViewModel> Dependencies { get; set; } = [];

    public bool HasDependencies => Dependencies.Count > 0;

    /// <summary>Images from the description.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGallery), nameof(GalleryHeader))]
    public partial IReadOnlyList<GalleryImageViewModel> Gallery { get; set; } = [];

    public bool HasGallery => Gallery.Count > 0;

    public string GalleryHeader => Gallery.Count == 0 ? "Gallery" : $"Gallery ({Gallery.Count})";

    /// <summary>"Latest compatible" (null), then each version that works in the selected game.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<string?>> VersionChoices { get; set; } = [new(null, "Latest compatible")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallText))]
    public partial Option<string?>? SelectedVersionChoice { get; set; }

    public string? SelectedVersion => SelectedVersionChoice?.Value;

    /// <summary>The main button: install, install a picked version, or switch the installed one to it.</summary>
    public string InstallText => (InstalledVersion, SelectedVersion) switch
    {
        (null, null) => "Install",
        (null, var v) => $"Install {v}",
        (var i, var v) when v is not null && v != i => $"Switch to {v}",
        _ => "Keep installed",
    };

    /// <summary>A different version is picked than the one installed.</summary>
    public bool CanSwitchVersion => IsInstalled && SelectedVersion is { } v && v != InstalledVersion;

    partial void OnSelectedVersionChoiceChanged(Option<string?>? value)
    {
        OnPropertyChanged(nameof(CanSwitchVersion));
        OnPropertyChanged(nameof(CanInstallHere));
    }

    partial void OnInstalledVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(CanSwitchVersion));
        OnPropertyChanged(nameof(CanInstallHere));
    }

    [ObservableProperty]
    public partial string? SizeText { get; set; }

    /// <summary>The selected game against the mod's versions, in a sentence or two.</summary>
    [ObservableProperty]
    public partial string? GameCompatibilityText { get; set; }

    public string ReleaseReportText => $"Players on the release branch: {Describe(Mod.CompatibilityRelease)}";

    public string ExperimentalReportText => $"Players on the experimental branch: {Describe(Mod.CompatibilityExperimental)}";

    private static string Describe(CompatibilityState state) => state switch
    {
        CompatibilityState.Works => "works",
        CompatibilityState.Damaged => "partly broken",
        CompatibilityState.Broken => "broken",
        _ => "no reports",
    };

    /// <summary>The newest version for the selected game, once looked up.</summary>
    [ObservableProperty]
    public partial string? LatestText { get; set; }
}

/// <summary>One version on a mod page's Versions tab.</summary>
public sealed class VersionRowViewModel(CatalogVersion version, bool supportsGame, bool isInstalled, long? size)
{
    public string Version { get; } = version.Version;

    public string Heading => version.Stability == VersionStability.Release ? Version : $"{Version} ({version.Stability})";

    public string DateText { get; } = version.CreatedAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public string GameText => $"Game {version.GameVersionRange}";

    public bool SupportsGame { get; } = supportsGame;

    public string SupportText => SupportsGame ? "Works with your game" : "Not for your game build";

    public bool IsInstalled { get; } = isInstalled;

    public string? SizeText { get; } = size is { } bytes ? SettingsViewModel.FormatSize(bytes) : null;

    public string Changelog { get; } = ModDescription.Readable(version.Changelog) is { Length: > 0 } text ? text : "No changelog given.";
}

/// <param name="Name">Display name, or the reference when the mod isn't in the catalogue.</param>
public sealed record DependencyRowViewModel(string Reference, string Name, string Condition, bool Optional, bool IsInstalled)
{
    public string Detail => Optional ? $"{Condition} · optional" : Condition;
}

/// <summary>One image from a mod's description, loaded when the mod page opens.</summary>
public partial class GalleryImageViewModel(string url) : ViewModelBase
{
    public string Url { get; } = url;

    [ObservableProperty]
    public partial Bitmap? Image { get; set; }
}
