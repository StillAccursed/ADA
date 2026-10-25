using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overlord.Core.Games;
using Overlord.Infrastructure.Installing;
using Overlord.Platform.Detection;

namespace Overlord.ViewModels;

public partial class DashboardViewModel(IGameDetector detector, CatalogStatusViewModel catalog, ModInstallService installs)
    : ViewModelBase
{
    // Cheap (one `ps` call), and quick enough that the banner feels live.
    private static readonly TimeSpan GameWatchInterval = TimeSpan.FromSeconds(3);

    // Long enough for the game to save and quit on its own before it is forced.
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(15);

    private IReadOnlyList<GameProcess> _gameProcesses = [];

    public CatalogStatusViewModel Catalog { get; } = catalog;

    public ObservableCollection<GameInstallViewModel> Installs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsScanning { get; set; }

    public string StatusText => IsScanning
        ? "Looking for Satisfactory…"
        : Installs.Count switch
        {
            0 => "No Satisfactory install found. Overlord only manages copies installed through Steam or the Epic Games Launcher.",
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
            Installs.Clear();
            foreach (var install in found)
                Installs.Add(new GameInstallViewModel(install, installs) { IsGameRunning = IsGameRunning });
        }
        finally
        {
            IsScanning = false;
        }

        // For each game: finish any interrupted install, then the fast check.
        foreach (var install in Installs)
            _ = install.CheckAsync();
    }

    /// <summary>Watches for Satisfactory starting and stopping for as long as the app runs.</summary>
    public async Task WatchGameAsync(CancellationToken ct = default)
    {
        using var timer = new PeriodicTimer(GameWatchInterval);
        do
        {
            await RefreshGameStateAsync();
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
                CloseError = "Satisfactory didn't close. Quit it from the game's menu, then try again.";
            await RefreshGameStateAsync();
        }
        finally
        {
            IsClosingGame = false;
        }
    }

    private async Task RefreshGameStateAsync()
    {
        _gameProcesses = await Task.Run(GameProcesses.FindRunning);
        var running = _gameProcesses.Count > 0;
        if (running == IsGameRunning)
            return;

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
}
