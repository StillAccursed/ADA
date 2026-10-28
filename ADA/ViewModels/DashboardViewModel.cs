using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ADA.Core.Games;
using ADA.Platform.Detection;

namespace ADA.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private static readonly TimeSpan CLOSE_GRACE_PERIOD = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _watchInterval;
    private readonly Func<CancellationToken, Task<IReadOnlyList<GameProcess>>> _findRunning;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IGameDetector _detector;
    private readonly Func<GameInstall, GameInstallViewModel> _createInstall;
    private readonly ILogger<DashboardViewModel> _log;
    private IReadOnlyList<GameProcess> _gameProcesses = [];

    public CatalogStatusViewModel Catalog { get; }

    public ObservableCollection<GameInstallViewModel> Installs { get; } = [];

    /// <summary>Any game has mod updates; shown as an arrow on the Dashboard's nav button.</summary>
    [ObservableProperty]
    public partial bool HasUpdates { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsScanning { get; set; }

    public string StatusText => IsScanning
        ? "Looking for Satisfactory…"
        : Installs.Count switch
        {
            0 => "No Satisfactory install found. ADA only manages copies installed through Steam or the Epic Games Launcher.",
            1 => "1 install found.",
            var n => $"{n} installs found.",
        };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloseOffer))]
    public partial bool IsGameRunning { get; set; }

    /// <summary>The user clicked "Close Satisfactory" and is being asked to confirm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloseOffer))]
    public partial bool ConfirmingClose { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloseOffer))]
    public partial bool IsClosingGame { get; set; }

    [ObservableProperty]
    public partial string? CloseError { get; set; }

    public bool ShowCloseOffer => IsGameRunning && !ConfirmingClose && !IsClosingGame;

    public DashboardViewModel(IGameDetector detector, CatalogStatusViewModel catalog,
        Func<GameInstall, GameInstallViewModel> installViewModel, ILogger<DashboardViewModel> log,
        Func<CancellationToken, Task<IReadOnlyList<GameProcess>>>? findRunning = null,
        TimeSpan? watchInterval = null,
        ADA.Infrastructure.Catalog.ModCatalogStore? store = null,
        ADA.Infrastructure.Settings.SettingsStore? settings = null,
        ADA.Infrastructure.Profiles.GameProfiles? profiles = null,
        ADA.Infrastructure.Installing.ModResolutionService? resolution = null,
        ADA.Infrastructure.IO.DashboardStorageReader? storage = null,
        ADA.Infrastructure.Installing.ModInstallService? installs = null)
    {
        _detector = detector;
        Catalog = catalog;
        _createInstall = installViewModel;
        _log = log;
        _watchInterval = watchInterval ?? TimeSpan.FromSeconds(3);
        _findRunning = findRunning ?? GameProcesses.FindRunningAsync;
        _store = store;
        _settings = settings;
        _profiles = profiles;
        _resolution = resolution;
        _storage = storage;
        _uiContext = SynchronizationContext.Current;
        if (settings is not null)
        {
            settings.Changed += OnCardSourceChanged;
        }
        if (profiles is not null)
        {
            profiles.Changed += OnCardSourceChanged;
        }
        if (installs is not null)
        {
            installs.Changed += OnCardSourceChanged;
        }
        if (catalog is not null)
        {
            catalog.PropertyChanged += OnCatalogChanged;
        }
    }

    /// <summary>Looks for mod updates in every game; run after the catalogue has been checked. Never throws.</summary>
    public Task FindUpdatesAsync() => Task.WhenAll(Installs.ToList().Select(i => i.FindUpdatesAsync()));

    /// <summary>
    /// Watches for Satisfactory starting and stopping for as long as the app runs. A failed check is
    /// logged and retried on the next tick, so the banner (which guards installs) never goes stale.
    /// </summary>
    public async Task WatchGameAsync(CancellationToken ct = default)
    {
        using var timer = new PeriodicTimer(_watchInterval);
        var failing = false;
        do
        {
            try
            {
                await RefreshGameStateAsync(ct);
                failing = false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Log once per outage, not every 3 seconds.
                if (!failing)
                {
                    _log.LogWarning(e, "Couldn't check whether Satisfactory is running");
                }
                failing = true;
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    partial void OnIsGameRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanChooseProfile));
        OnPropertyChanged(nameof(GameStateText));
    }

    private void OnInstallChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        HasUpdates = Installs.Any(i => i.HasUpdates);
        OnPropertyChanged(nameof(CanChooseProfile));
        OnPropertyChanged(nameof(Notifications));
        if (e.PropertyName == nameof(GameInstallViewModel.IsBusy) && sender is GameInstallViewModel { IsBusy: false })
        {
            _ = RefreshCardsAsync();
        }
    }

    /// <summary>Retains the selected game across detection and starts guarded recovery checks.</summary>
    [RelayCommand]
    private async Task ScanAsync()
    {
        if (Installs.Any(i => i.IsBusy || i.HasModOperations || i.IsSwitchingProfile))
        {
            ScanError = "Wait for the current installation operation before rescanning.";
            return;
        }
        var selectedPath = SelectedInstall?.Install.GamePath;
        IsScanning = true;
        ScanError = null;
        try
        {
            // Detection reads registry hives and walks folders; keep it off the UI thread.
            var found = await Task.Run(_detector.Detect);
            foreach (var old in Installs)
            {
                old.PropertyChanged -= OnInstallChanged;
            }
            Installs.Clear();
            foreach (var install in found)
            {
                var vm = _createInstall(install);
                vm.IsGameRunning = IsGameRunning;
                vm.PropertyChanged += OnInstallChanged;
                Installs.Add(vm);
            }
            SelectedInstall = Installs.FirstOrDefault(i => i.Install.GamePath == selectedPath) ?? Installs.FirstOrDefault();
            HasUpdates = false;
            _log.LogInformation("Found {Count} Satisfactory install(s): {Paths}", found.Count, found.Select(f => f.GamePath));
        }
        catch (Exception e)
        {
            // Detectors shouldn't throw, but a failed scan must never take the app down.
            _log.LogError(e, "Game detection failed");
            ScanError = "Couldn’t detect installations: " + e.Message;
        }
        finally
        {
            IsScanning = false;
        }

        // For each game: finish any interrupted install, then the fast check.
        foreach (var install in Installs)
        {
            _ = install.CheckAsync();
        }
        await RefreshCardsAsync();
    }

    [RelayCommand]
    private void CloseGame()
    {
        CloseError = null;
        ConfirmingClose = true;
    }

    [RelayCommand]
    private void CancelCloseGame() => ConfirmingClose = false;

    [RelayCommand]
    private async Task ConfirmCloseGameAsync()
    {
        ConfirmingClose = false;
        IsClosingGame = true;
        try
        {
            var closed = await GameProcesses.CloseAsync(_gameProcesses, CLOSE_GRACE_PERIOD);
            if (!closed)
            {
                _log.LogWarning("Satisfactory didn't close within {Grace}", CLOSE_GRACE_PERIOD);
                CloseError = "Satisfactory didn't close. Quit it from the game's menu, then try again.";
            }
            await RefreshGameStateAsync();
        }
        catch (Exception e)
        {
            _log.LogError(e, "Closing Satisfactory failed");
            CloseError = $"Couldn't close Satisfactory: {e.Message}";
        }
        finally
        {
            IsClosingGame = false;
        }
    }

    private async Task RefreshGameStateAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            _gameProcesses = await _findRunning(ct);
            var running = _gameProcesses.Count > 0;
            if (running == IsGameRunning)
            {
                return;
            }

            _log.LogInformation(running ? "Satisfactory started" : "Satisfactory closed");
            IsGameRunning = running;
            if (!running)
            {
                ConfirmingClose = false;
            }

            foreach (var install in Installs)
            {
                install.IsGameRunning = running;
                // Just closed: finish any recovery that was waiting, and re-check.
                if (!running)
                {
                    _ = install.CheckAsync();
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
