using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ADA.Core.Catalog;
using ADA.Core.Profiles;
using ADA.Core.Settings;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;

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
    private readonly ModCatalogStore? _store;
    private readonly SettingsStore? _settings;
    private readonly GameProfiles? _profiles;
    private readonly ModResolutionService? _resolution;
    private readonly DashboardStorageReader? _storage;
    private SynchronizationContext? _uiContext;
    private CancellationTokenSource? _cardRefresh;
    private bool _loadingProfiles;
    private int _cardRevision;
    private int _requestedRecentCount;
    private bool _hasImplicitDefault;

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
        ? _hasImplicitDefault && profile.Name.Equals(ProfileStore.DefaultName, StringComparison.OrdinalIgnoreCase)
            ? "Default has not been saved yet." : $"{profile.Mods.Count} mods in profile"
        : "No profile selected";

    public string LastCatalogUpdateText => Catalog.LastChecked?.ToLocalTime().ToString("g") ?? "Not checked yet";

    public bool CanChooseProfile => SelectedInstall is { IsBusy: false, IsSwitchingProfile: false, HasModOperations: false }
        && !IsGameRunning && _profiles is not null && !_profiles.IsChanging(SelectedInstall.Install);

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

    /// <summary>Completes after the most recent dropdown-triggered profile switch and refresh.</summary>
    internal Task PendingProfileSwitch { get; private set; } = Task.CompletedTask;

    /// <summary>Reads cached releases, profiles and storage; newer selections cancel and supersede old snapshots.</summary>
    public async Task RefreshCardsAsync()
    {
        _uiContext ??= SynchronizationContext.Current;
        var recentCount = _settings?.Current.DashboardLatestModsCount ?? AppSettings.DEFAULT_DASHBOARD_LATEST_MODS_COUNT;
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
            var recentTask = Task.Run(() => _store?.GetRecentlyUpdated(recentCount), ct);
            var profileTask = _profiles?.ListAsync() ?? Task.FromResult<IReadOnlyList<ModProfile>>([]);
            var diskTask = ReadStorageAsync(game?.Install.ModsPath, ct);
            await Task.WhenAll(recentTask, profileTask, diskTask);
            ct.ThrowIfCancellationRequested();
            if (revision != _cardRevision)
            {
                return;
            }
            if (await recentTask is { } recent)
            {
                RecentMods.Clear();
                foreach (var mod in recent)
                {
                    RecentMods.Add(new(mod));
                }
                RecentModsState = recent.Count == 0 ? "No cached releases yet." : "";
            }
            Disk = new(await diskTask);
            if (game?.IsSwitchingProfile != true)
            {
                _loadingProfiles = true;
                try
                {
                    var activeName = game is null || _profiles is null
                        ? ProfileStore.DefaultName : await _profiles.ActiveNameAsync(game.Install);
                    ct.ThrowIfCancellationRequested();
                    Profiles.Clear();
                    foreach (var profile in await profileTask)
                    {
                        Profiles.Add(profile);
                    }
                    _hasImplicitDefault = !Profiles.Any(p => p.Name.Equals(ProfileStore.DefaultName, StringComparison.OrdinalIgnoreCase));
                    if (_hasImplicitDefault)
                    {
                        Profiles.Insert(0, new(ProfileStore.DefaultName, []));
                    }
                    SelectedProfile = game is null ? null : Profiles.FirstOrDefault(p => p.Name.Equals(activeName, StringComparison.OrdinalIgnoreCase));
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
            PendingProfileSwitch = SwitchProfileAsync(game, value);
        }
    }

    private async Task SwitchProfileAsync(GameInstallViewModel game, ModProfile profile)
    {
        if (!CanChooseProfile || _profiles is null || _resolution is null)
        {
            ProfileNotice = "Wait for current operations to finish and close Satisfactory before switching profiles.";
            await RefreshCardsAsync();
            return;
        }
        game.IsSwitchingProfile = true;
        game.IsBusy = true;
        ProfileNotice = $"Switching to {profile.Name}…";
        try
        {
            var progress = new Progress<ModInstallProgress>(p => game.ProgressText = PlanText.Progress(p));
            await _profiles.SwitchAsync(game.Install, profile, _resolution, progress);
            game.ProfileName = profile.Name;
            ProfileNotice = $"{profile.Name} is active for {game.Install.DisplayName}.";
        }
        catch (Exception e)
        {
            ProfileNotice = e.Message;
        }
        finally
        {
            game.ProgressText = null;
            game.IsSwitchingProfile = false;
            game.IsBusy = false;
            await game.CheckAsync();
            await RefreshCardsAsync();
        }
    }

    private async Task<DashboardStorageSnapshot?> ReadStorageAsync(string? path, CancellationToken ct)
    {
        return _storage is null ? null : await _storage.ReadAsync(path, ct);
    }

    private void OnCardSourceChanged(object? sender, EventArgs e)
    {
        void RefreshFromSource()
        {
            if (ReferenceEquals(sender, _settings) && _settings?.Current.DashboardLatestModsCount == _requestedRecentCount)
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
