using System.Collections.ObjectModel;
using System.ComponentModel;
using ADA.Core.Catalog;
using ADA.Core.Profiles;
using ADA.Core.Settings;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADA.ViewModels;

/// <summary>One cached release; no version-history request is needed to render it.</summary>
public sealed class DashboardModViewModel(CatalogMod mod)
{
    public string Name => mod.Name;

    public string Description => mod.ShortDescription;

    public string Version => mod.Latest?.FirstOrDefault(v => v.Stability == VersionStability.Release)?.Version
        ?? "Version unavailable";

    public string Released => mod.LastVersionDate?.ToLocalTime().ToString("g") ?? "Release date unavailable";
}

public partial class DashboardViewModel
{
    private readonly ModCatalogStore _store;
    private readonly SettingsStore _settings;
    private readonly GameProfiles _profiles;
    private readonly ModResolutionService _resolution;
    private readonly DashboardStorageReader _storage;
    private readonly ModChangelogs _changelogs;

    // Installations whose create-profile prompt was dismissed this session; "New profile" still opens it.
    private readonly HashSet<string> _promptDismissed = new(StringComparer.Ordinal);
    private SynchronizationContext? _uiContext;
    private CancellationTokenSource? _cardRefresh;
    private bool _loadingProfiles;
    private int _cardRevision;
    private int _requestedRecentCount;

    public ObservableCollection<DashboardModViewModel> RecentMods { get; } = [];

    public ObservableCollection<ModProfile> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedInstall))]
    public partial GameInstallViewModel? SelectedInstall { get; set; }

    public bool HasSelectedInstall => SelectedInstall is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileModsText))]
    public partial ModProfile? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial DashboardDiskStatusViewModel Disk { get; private set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notifications))]
    public partial string? ScanError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notifications))]
    public partial string? CardError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notifications))]
    public partial string? ProfileNotice { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notifications))]
    internal partial string? OperationNotice { get; set; }

    [ObservableProperty]
    public partial string RecentModsState { get; private set; } = "Loading cached releases…";

    public string GameStateText => IsGameRunning ? "Running" : "Not running";

    public string ProfilesText => $"{Profiles.Count} profile{(Profiles.Count == 1 ? "" : "s")}";

    public string ProfileModsText => SelectedProfile is { } profile
        ? $"{profile.Mods.Count} mod{(profile.Mods.Count == 1 ? "" : "s")} in profile"
        : SelectedInstall is null ? "No installation selected" : "This installation has no profile yet.";

    /// <summary>
    /// The open create-profile prompt, or null. Set when the selected installation has no profile
    /// (always the case on first run) and by <see cref="NewProfileCommand"/>; the main window shows
    /// it as a dialog and it clears itself when closed.
    /// </summary>
    [ObservableProperty]
    public partial CreateProfileViewModel? ProfilePrompt { get; private set; }

    /// <summary>
    /// The open Switch profile dialog, or null. Set when another profile is chosen in the Profiles
    /// card; the main window shows it as a dialog and it clears itself when closed.
    /// </summary>
    [ObservableProperty]
    public partial SwitchProfileViewModel? SwitchPrompt { get; private set; }

    public string LastCatalogUpdateText => Catalog.LastChecked?.ToLocalTime().ToString("g") ?? "Not checked yet";

    public bool CanChooseProfile => SelectedInstall is { IsBusy: false, IsSwitchingProfile: false, HasModOperations: false }
        && !IsGameRunning && !_profiles.IsChanging(SelectedInstall.Install);

    /// <summary>Current actionable conditions, deduplicated without inventing notification history.</summary>
    public string Notifications
    {
        get
        {
            var lines = new List<string?> { ScanError, Catalog?.ErrorText, CardError, ProfileNotice, OperationNotice };
            foreach (var game in Installs)
            {
                var prefix = game.Install.DisplayName + ": ";
                if (!string.IsNullOrWhiteSpace(game.Notice))
                {
                    lines.Add(prefix + game.Notice);
                }
                if (game.HasRepairs)
                {
                    lines.Add(prefix + "Some mods need repair.");
                }
                if (game.HasFoundMods)
                {
                    lines.Add(prefix + "Found mods need your attention.");
                }
                if (game.HasFix || game.HasFixProblem)
                {
                    lines.Add(prefix + (game.FixProblem ?? "Some mods are missing dependencies."));
                }
                if (game.HasUpdates)
                {
                    lines.Add(prefix + game.UpdatesTitle);
                }
            }
            var notices = lines.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            return notices.Count == 0 ? "No action needed." : string.Join("\n\n", notices);
        }
    }

    /// <summary>
    /// Completes once the most recently chosen profile's switch preview is worked out (or refused), and
    /// again once its dialog has closed and the cards are refreshed.
    /// </summary>
    internal Task PendingProfileSwitch { get; private set; } = Task.CompletedTask;

    /// <summary>Reads cached releases, profiles and storage; newer selections cancel and supersede old snapshots.</summary>
    public async Task RefreshCardsAsync()
    {
        _uiContext ??= SynchronizationContext.Current;
        var recentCount = _settings.Current.DashboardLatestModsCount;
        _requestedRecentCount = recentCount;
        _cardRefresh?.Cancel();
        var source = new CancellationTokenSource();
        _cardRefresh = source;
        var ct = source.Token;
        var revision = ++_cardRevision;
        var game = SelectedInstall;
        try
        {
            // Coalesce bursty check/profile/operation notifications into one filesystem scan.
            await Task.Delay(100, ct);
            var recentTask = Task.Run(() => _store.GetRecentlyUpdated(recentCount), ct);
            var profileTask = _profiles.ListAsync();
            var diskTask = _storage.ReadAsync(game?.Install.ModsPath, ct);
            await Task.WhenAll(recentTask, profileTask, diskTask);
            ct.ThrowIfCancellationRequested();
            if (revision != _cardRevision)
            {
                return;
            }
            var recent = await recentTask;
            RecentMods.Clear();
            foreach (var mod in recent)
            {
                RecentMods.Add(new(mod));
            }
            RecentModsState = recent.Count == 0 ? "No cached releases yet." : "";
            Disk = new(await diskTask);
            if (game?.IsSwitchingProfile != true)
            {
                _loadingProfiles = true;
                string? activeName;
                try
                {
                    activeName = game is null ? null : await _profiles.ActiveNameAsync(game.Install);
                    ct.ThrowIfCancellationRequested();
                    Profiles.Clear();
                    foreach (var profile in await profileTask)
                    {
                        Profiles.Add(profile);
                    }
                    SelectedProfile = activeName is null
                        ? null : Profiles.FirstOrDefault(p => p.Name.Equals(activeName, StringComparison.OrdinalIgnoreCase));
                    if (game is not null)
                    {
                        game.ProfileName = activeName;
                    }
                    OnPropertyChanged(nameof(ProfilesText));
                    OnPropertyChanged(nameof(ProfileModsText));
                    OnPropertyChanged(nameof(CanChooseProfile));
                }
                finally
                {
                    _loadingProfiles = false;
                }
                if (game is { IsBusy: false } && activeName is null && !_promptDismissed.Contains(game.Install.GamePath))
                {
                    OpenProfilePrompt(game);
                }
            }
            CardError = null;
        }
        catch (OperationCanceledException)
        {
            // The previous snapshot belongs to an old selection or refresh.
        }
        catch (Exception e)
        {
            if (revision == _cardRevision)
            {
                CardError = "Couldn't refresh dashboard: " + e.Message;
                if (RecentMods.Count == 0)
                {
                    RecentModsState = "Cached releases are unavailable.";
                }
            }
        }
        finally
        {
            source.Dispose();
            if (ReferenceEquals(_cardRefresh, source))
            {
                _cardRefresh = null;
            }
        }
    }

    /// <summary>Called from application startup on the UI thread; services are constructed on a worker.</summary>
    internal void AttachUiContext() => _uiContext = SynchronizationContext.Current;

    /// <summary>Retains failures from this operation batch until the user starts a new batch.</summary>
    internal void ReportOperationProblem(string problem)
    {
        OperationNotice = string.IsNullOrEmpty(OperationNotice) ? problem : OperationNotice + "\n" + problem;
    }

    partial void OnSelectedInstallChanged(GameInstallViewModel? value)
    {
        ProfileNotice = null;
        OnPropertyChanged(nameof(CanChooseProfile));
        Disk = new();
        _ = RefreshCardsAsync();
    }

    partial void OnSelectedProfileChanged(ModProfile? value)
    {
        if (!_loadingProfiles && value is not null && SelectedInstall is { } game
            && !string.Equals(game.ProfileName, value.Name, StringComparison.OrdinalIgnoreCase))
        {
            PendingProfileSwitch = OpenSwitchPromptAsync(game, value);
        }
    }

    /// <summary>Opens the create-profile prompt for the selected installation, even if it was dismissed.</summary>
    [RelayCommand]
    private void NewProfile()
    {
        if (SelectedInstall is not { } game || !CanChooseProfile)
        {
            ProfileNotice = "Wait for current operations to finish and close Satisfactory before creating a profile.";
            return;
        }
        OpenProfilePrompt(game);
    }

    /// <summary>Shows one prompt at a time; dismissing it stops the automatic prompt for that installation.</summary>
    private void OpenProfilePrompt(GameInstallViewModel game)
    {
        if (ProfilePrompt is not null)
        {
            return;
        }
        var prompt = new CreateProfileViewModel(game, _profiles, Profiles.Select(p => p.Name).ToList());
        prompt.Closed += (_, created) =>
        {
            ProfilePrompt = null;
            if (created)
            {
                ProfileNotice = $"{game.ProfileName} is active for {game.Install.DisplayName}.";
            }
            else
            {
                _promptDismissed.Add(game.Install.GamePath);
            }
            _ = RefreshCardsAsync();
        };
        ProfilePrompt = prompt;
    }

    /// <summary>
    /// Shows what switching to <paramref name="profile"/> would change, instead of switching at once.
    /// The installation is marked as switching while the dialog is open, so nothing else starts on it;
    /// closing restores the dropdown to the active profile unless the switch succeeded.
    /// </summary>
    private async Task OpenSwitchPromptAsync(GameInstallViewModel game, ModProfile profile)
    {
        if (!CanChooseProfile || SwitchPrompt is not null)
        {
            ProfileNotice = "Wait for current operations to finish and close Satisfactory before switching profiles.";
            await RefreshCardsAsync();
            return;
        }
        var prompt = new SwitchProfileViewModel(game, profile, game.ProfileName, _profiles, _resolution, _changelogs.ForRangeAsync,
            () => !IsGameRunning && !_profiles.IsChanging(game.Install));
        game.IsSwitchingProfile = true;
        ProfileNotice = null;
        prompt.Closed += (_, switched) => PendingProfileSwitch = CloseSwitchPromptAsync(game, profile, switched);
        SwitchPrompt = prompt;
        await prompt.PreviewAsync();
    }

    /// <summary>
    /// Releases the installation once the Switch profile dialog closes, reports a successful switch, and
    /// re-reads the cards, which also puts the dropdown back on the active profile after Cancel.
    /// </summary>
    private async Task CloseSwitchPromptAsync(GameInstallViewModel game, ModProfile profile, bool switched)
    {
        SwitchPrompt = null;
        game.IsSwitchingProfile = false;
        if (switched)
        {
            ProfileNotice = $"{profile.Name} is active for {game.Install.DisplayName}.";
        }
        await game.CheckAsync();
        await RefreshCardsAsync();
    }

    private void OnCardSourceChanged(object? sender, EventArgs e)
    {
        void RefreshFromSource()
        {
            if (ReferenceEquals(sender, _settings) && _settings.Current.DashboardLatestModsCount == _requestedRecentCount)
            {
                return;
            }
            _ = RefreshCardsAsync();
        }
        if (_uiContext is { } context && SynchronizationContext.Current != context)
        {
            context.Post(_ => RefreshFromSource(), null);
        }
        else
        {
            RefreshFromSource();
        }
    }

    private void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(LastCatalogUpdateText));
        OnPropertyChanged(nameof(Notifications));
        if (e.PropertyName == nameof(CatalogStatusViewModel.IsChecking) && !Catalog.IsChecking)
        {
            _ = RefreshCardsAsync();
        }
    }
}
