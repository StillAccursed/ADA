using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ADA.Core.Games;
using ADA.Platform.Detection;

namespace ADA.ViewModels;

public partial class DashboardViewModel(
    IGameDetector detector,
    CatalogStatusViewModel catalog,
    Func<GameInstall, GameInstallViewModel> installViewModel,
    ILogger<DashboardViewModel> log,
    Func<CancellationToken, Task<IReadOnlyList<GameProcess>>>? findRunning = null,
    TimeSpan? watchInterval = null)
    : ViewModelBase
{
    // Cheap (one `ps` call), and quick enough that the banner feels live.
    private readonly TimeSpan _watchInterval = watchInterval ?? TimeSpan.FromSeconds(3);

    // Long enough for the game to save and quit on its own before it is forced.
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task<IReadOnlyList<GameProcess>>> _findRunning =
        findRunning ?? GameProcesses.FindRunningAsync;

    // The watcher and "Close Satisfactory" both refresh; one at a time keeps the state consistent.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private IReadOnlyList<GameProcess> _gameProcesses = [];

    public CatalogStatusViewModel Catalog { get; } = catalog;

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

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        try
        {
            // Detection reads registry hives and walks folders; keep it off the UI thread.
            var found = await Task.Run(detector.Detect);
            foreach (var old in Installs)
                old.PropertyChanged -= OnInstallChanged;
            Installs.Clear();
            foreach (var install in found)
            {
                var vm = installViewModel(install);
                vm.IsGameRunning = IsGameRunning;
                vm.PropertyChanged += OnInstallChanged;
                Installs.Add(vm);
            }
            HasUpdates = false;
            log.LogInformation("Found {Count} Satisfactory install(s): {Paths}", found.Count, found.Select(f => f.GamePath));
        }
        catch (Exception e)
        {
            // Detectors shouldn't throw, but a failed scan must never take the app down.
            log.LogError(e, "Game detection failed");
        }
        finally
        {
            IsScanning = false;
        }

        // For each game: finish any interrupted install, then the fast check.
        foreach (var install in Installs)
            _ = install.CheckAsync();
    }

    /// <summary>Looks for mod updates in every game; run after the catalogue has been checked. Never throws.</summary>
    public Task FindUpdatesAsync() => Task.WhenAll(Installs.ToList().Select(i => i.FindUpdatesAsync()));

    private void OnInstallChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameInstallViewModel.HasUpdates))
            HasUpdates = Installs.Any(i => i.HasUpdates);
    }

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
                    log.LogWarning(e, "Couldn't check whether Satisfactory is running");
                failing = true;
            }
        } while (await timer.WaitForNextTickAsync(ct));
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
            var closed = await GameProcesses.CloseAsync(_gameProcesses, CloseGracePeriod);
            if (!closed)
            {
                log.LogWarning("Satisfactory didn't close within {Grace}", CloseGracePeriod);
                CloseError = "Satisfactory didn't close. Quit it from the game's menu, then try again.";
            }
            await RefreshGameStateAsync();
        }
        catch (Exception e)
        {
            log.LogError(e, "Closing Satisfactory failed");
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
                return;

            log.LogInformation(running ? "Satisfactory started" : "Satisfactory closed");
            IsGameRunning = running;
            if (!running)
                ConfirmingClose = false;

            foreach (var install in Installs)
            {
                install.IsGameRunning = running;
                // Just closed: finish any recovery that was waiting, and re-check.
                if (!running)
                    _ = install.CheckAsync();
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
