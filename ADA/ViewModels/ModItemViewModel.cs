using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ADA.Core.Catalog;
using ADA.Infrastructure.Catalog;

namespace ADA.ViewModels;

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

    /// <summary>
    /// Markdown and structural HTML for the Overview, prepared on first use without author CSS or
    /// images. Falls back to the short description when the full description contains no content.
    /// </summary>
    public string Description => _description ??= ModDescription.Formatted(Mod.FullDescription) is { Length: > 0 } text
        ? text
        : Mod.ShortDescription;

    /// <summary>Ficsit's current EA report for Stable, independent of the selected mod version and local game.</summary>
    public BranchCompatibilityViewModel StableCompatibility { get; } = new(mod.CompatibilityRelease);

    /// <summary>Ficsit's current EXP report, independent of the selected mod version and local game.</summary>
    public BranchCompatibilityViewModel ExperimentalCompatibility { get; } = new(mod.CompatibilityExperimental);

    /// <summary>The version installed in the selected game, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(InstalledText), nameof(CanBeRequested), nameof(CanInstallHere))]
    [NotifyPropertyChangedFor(nameof(QuickActionText), nameof(IsUninstallAction), nameof(ShowUninstall))]
    public partial string? InstalledVersion { get; set; }

    /// <summary>Installed only because another mod needs it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText), nameof(CanBeRequested), nameof(CanInstallHere))]
    public partial bool IsDependencyOnly { get; set; }

    public bool IsInstalled => InstalledVersion is not null;

    /// <summary>True for a captured removal request, otherwise follows the current installed state.</summary>
    public bool IsUninstallAction => PendingUninstall ?? IsInstalled;

    /// <summary>The request captured for this mod in the displayed game; null when none is pending.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickActionText), nameof(InstallText), nameof(UninstallText), nameof(IsUninstallAction), nameof(CanInstallHere), nameof(ShowUninstall))]
    public partial bool? PendingUninstall { get; internal set; }

    /// <summary>One-based position in the execution order; zero when this mod has no submitted request.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickActionText), nameof(InstallText), nameof(UninstallText))]
    public partial int OperationPosition { get; internal set; }

    /// <summary>This mod's captured operation is currently being resolved, applied or refreshed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickActionText), nameof(InstallText), nameof(UninstallText))]
    public partial bool IsOperationRunning { get; internal set; }

    /// <summary>New operations join existing work rather than starting immediately.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickActionText), nameof(InstallText), nameof(UninstallText))]
    public partial bool IsOperationQueueBusy { get; internal set; }

    /// <summary>The quick action, or the captured request's running state and queue position.</summary>
    public string QuickActionText => OperationText ?? (IsOperationQueueBusy ? QueueActionText : ActionText);

    private string ActionText => IsUninstallAction ? "Uninstall" : "Install";

    private string QueueActionText => IsUninstallAction ? "Queue uninstall" : "Queue install";

    /// <summary>The details-page removal action, including its submitted request's status.</summary>
    public string UninstallText => PendingUninstall is true ? OperationText! : IsOperationQueueBusy ? "Queue uninstall" : "Uninstall";

    private string? OperationText => PendingUninstall switch
    {
        null => null,
        true when IsOperationRunning => "Uninstalling…",
        false when IsOperationRunning => "Installing…",
        true => $"Uninstall queued · #{OperationPosition}",
        false => $"Install queued · #{OperationPosition}",
    };

    /// <summary>Install is offered: not installed, or only there as a dependency (installing keeps it).</summary>
    public bool CanBeRequested => !IsInstalled || IsDependencyOnly;

    /// <summary>Offers installation or version switching, and keeps a captured install visible until it completes.</summary>
    public bool CanInstallHere => CanBeRequested || CanSwitchVersion || PendingUninstall is false;

    /// <summary>Keeps a captured removal visible even if an earlier request already removed this mod.</summary>
    public bool ShowUninstall => IsInstalled || PendingUninstall is true;

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

    /// <summary>The details-page install/version action, including its submitted request's status.</summary>
    public string InstallText => PendingUninstall is false ? OperationText!
        : IsOperationQueueBusy ? $"Queue {char.ToLowerInvariant(BaseInstallText[0])}{BaseInstallText[1..]}" : BaseInstallText;

    private string BaseInstallText => (InstalledVersion, SelectedVersion) switch
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
