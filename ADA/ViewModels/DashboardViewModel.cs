using System.Collections.ObjectModel;
using ADA.Core.Games;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Servers;
using ADA.Infrastructure.Settings;
using ADA.Platform.Detection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    // The server's API is asked whether it's running every this many process checks (15 seconds by default).
    private const int SERVER_POLL_TICKS = 5;

    private static readonly TimeSpan CLOSE_GRACE_PERIOD = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _watchInterval;
    private readonly Func<CancellationToken, Task<IReadOnlyList<GameProcess>>> _findRunning;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IGameDetector _detector;
    private readonly Func<GameInstall, GameInstallViewModel> _createInstall;
    private readonly ILogger<DashboardViewModel> _log;
    private readonly ServerConnections? _servers;
    private readonly ProfileSync? _sync;
    private readonly ConfigPush? _configPush;
    private readonly ModInstallService _installs;
    private IReadOnlyList<GameProcess> _gameProcesses = [];

    // The connected server's install, kept apart from detection so a rescan doesn't drop it. Null while no server is connected.
    private GameInstallViewModel? _serverInstall;

    // An install asked for by SelectInstall before it was listed; selected as soon as it appears.
    private string? _pendingSelection;

    public CatalogStatusViewModel Catalog { get; }

    public ObservableCollection<GameInstallViewModel> Installs { get; } = [];

    /// <summary>Any game has mod updates; shown as an arrow on the Dashboard's nav button.</summary>
    [ObservableProperty]
    public partial bool HasUpdates { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(GameStateText))]
    public partial bool IsScanning { get; set; }

    public string StatusText => IsScanning
        ? "Looking for Satisfactory…"
        : Installs.Count(i => !i.Install.IsRemote) switch
        {
            0 => "No Satisfactory install found. ADA only manages copies installed through Steam or the Epic Games Launcher.",
            1 => "1 install found.",
            var n => $"{n} installs found.",
        };

    /// <summary>A local Satisfactory process is running. Each install, including a server, also has its own state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloseOffer))]
    public partial bool IsGameRunning { get; set; }

    /// <summary>The connected server can't be reached; shown on the Needs Attention card whichever install is selected.</summary>
    public string? ServerProblem => _servers is { State: ServerState.Unavailable, Server: { } server }
        ? $"{server.Name} is unavailable: {_servers.Problem}"
        : null;

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

    /// <summary>
    /// Subscribes to settings, profile, installation, archive and catalogue changes so the cards
    /// refresh themselves; those events are marshalled to the UI context captured here or by
    /// <see cref="AttachUiContext"/>.
    /// </summary>
    /// <param name="findRunning">Process lookup; tests replace it. Defaults to <see cref="GameProcesses.FindRunningAsync"/>.</param>
    /// <param name="watchInterval">How often <see cref="WatchGameAsync"/> checks; three seconds by default.</param>
    /// <param name="servers">The server connection; its install joins <see cref="Installs"/> while connected. Null where servers aren't used.</param>
    public DashboardViewModel(IGameDetector detector, CatalogStatusViewModel catalog,
        Func<GameInstall, GameInstallViewModel> installViewModel, ILogger<DashboardViewModel> log,
        ModCatalogStore store, SettingsStore settings, GameProfiles profiles, ModResolutionService resolution,
        DashboardStorageReader storage, ModInstallService installs, ArchiveCache archives, ModChangelogs changelogs,
        Func<CancellationToken, Task<IReadOnlyList<GameProcess>>>? findRunning = null,
        TimeSpan? watchInterval = null, ServerConnections? servers = null, ProfileSync? sync = null, ConfigPush? configPush = null)
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
        _changelogs = changelogs;
        _installs = installs;
        _servers = servers;
        _sync = sync;
        _configPush = configPush;
        _uiContext = SynchronizationContext.Current;
        settings.Changed += OnCardSourceChanged;
        profiles.Changed += OnCardSourceChanged;
        installs.Changed += OnCardSourceChanged;
        archives.Changed += OnCardSourceChanged;
        catalog.PropertyChanged += OnCatalogChanged;
        if (servers is not null)
        {
            servers.Changed += OnServersChanged;
        }
    }

    /// <summary>Looks for mod updates in every game; run after the catalogue has been checked. Never throws.</summary>
    public Task FindUpdatesAsync() => Task.WhenAll(Installs.ToList().Select(i => i.FindUpdatesAsync()));

    /// <summary>
    /// Selects the install with this key, e.g. a server the user just chose to switch to. If it isn't
    /// listed yet (its connection is still being reported), it's selected as soon as it is.
    /// </summary>
    public void SelectInstall(string gamePath)
    {
        if (Installs.FirstOrDefault(i => i.Install.GamePath == gamePath) is { } install)
        {
            _pendingSelection = null;
            SelectedInstall = install;
        }
        else
        {
            _pendingSelection = gamePath;
        }
    }

    /// <summary>
    /// Watches for Satisfactory starting and stopping for as long as the app runs. A failed check is
    /// logged and retried on the next tick, so the banner (which guards installs) never goes stale.
    /// </summary>
    public async Task WatchGameAsync(CancellationToken ct = default)
    {
        using var timer = new PeriodicTimer(_watchInterval);
        var failing = false;
        var tick = 0;
        do
        {
            try
            {
                await RefreshGameStateAsync(ct);
                if (tick++ % SERVER_POLL_TICKS == 0)
                {
                    await RefreshServerStateAsync(ct);
                }
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
        SwitchPrompt?.RefreshCanSwitch();
    }

    /// <summary>
    /// Recomputes the Dashboard's summaries when an installation's state changes. Progress text is
    /// skipped: it changes many times a second during downloads and affects none of them.
    /// </summary>
    private void OnInstallChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameInstallViewModel.ProgressText))
        {
            return;
        }
        HasUpdates = Installs.Any(i => i.HasUpdates);
        OnPropertyChanged(nameof(CanChooseProfile));
        OnPropertyChanged(nameof(Notifications));
        OnPropertyChanged(nameof(ServerStateText));
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
            foreach (var old in Installs.Where(i => !ReferenceEquals(i, _serverInstall)))
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
            if (_serverInstall is { } server)
            {
                Installs.Add(server);
            }
            SelectedInstall = Installs.FirstOrDefault(i => i.Install.GamePath == selectedPath) ?? Installs.FirstOrDefault();
            HasUpdates = false;
            _log.LogInformation("Found {Count} Satisfactory install(s): {Paths}", found.Count, found.Select(f => f.GamePath));
            OnPropertyChanged(nameof(StatusText));
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

            foreach (var install in Installs.Where(i => !i.Install.IsRemote))
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

    /// <summary>Tries the unavailable server again now, rather than waiting for the next background attempt.</summary>
    [RelayCommand]
    private Task RetryServerAsync() => _servers?.RetryAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Asks the connected server whether it's running; its own state, separate from local processes.
    /// Whether it's reachable at all is checked by <see cref="ServerConnections"/> on its own timer,
    /// so a slow server never holds up the local check.
    /// </summary>
    private async Task RefreshServerStateAsync(CancellationToken ct)
    {
        if (_servers is not null && _serverInstall is { IsUnavailable: false } server)
        {
            var running = await _servers.IsRunningAsync(server.Install, ct);
            // The server may have been replaced while the API answered.
            if (ReferenceEquals(server, _serverInstall) && server.IsGameRunning != running)
            {
                server.IsGameRunning = running;
                if (!running)
                {
                    _ = server.CheckAsync();
                }
            }
        }
    }

    private void OnServersChanged(object? sender, EventArgs e)
    {
        if (_uiContext is { } context && SynchronizationContext.Current != context)
        {
            context.Post(_ => UpdateServerInstall(), null);
        }
        else
        {
            UpdateServerInstall();
        }
    }

    /// <summary>
    /// Brings the server's entry in line with the connection: added after the local installs while a
    /// server is connected, connecting or unavailable, and removed when none is. A replaced entry stays
    /// selected if its predecessor was; a removed one hands the selection to the first local install.
    /// </summary>
    private void UpdateServerInstall()
    {
        OnPropertyChanged(nameof(ServerProblem));
        OnPropertyChanged(nameof(Notifications));
        var wanted = _servers switch
        {
            { State: ServerState.Connected, Install: { } install } => install,
            { State: ServerState.Connecting or ServerState.Unavailable, Server: { } server } => _servers.Install ?? ServerConnections.ToInstall(server, null),
            _ => null,
        };

        var old = _serverInstall;
        var wasSelected = old is not null && ReferenceEquals(SelectedInstall, old);
        if (old is not null && old.Install != wanted)
        {
            old.PropertyChanged -= OnInstallChanged;
            Installs.Remove(old);
            _serverInstall = null;
        }
        if (wanted is not null && _serverInstall is null)
        {
            var vm = _createInstall(wanted);
            vm.PropertyChanged += OnInstallChanged;
            Installs.Add(vm);
            _serverInstall = vm;
        }

        if (_serverInstall is { } current)
        {
            current.UnavailableReason = _servers!.State switch
            {
                ServerState.Connecting => "Still connecting.",
                ServerState.Unavailable => _servers.Problem ?? "It can't be reached.",
                _ => null,
            };
            if ((wasSelected && !ReferenceEquals(old, current)) || current.Install.GamePath == _pendingSelection)
            {
                _pendingSelection = null;
                SelectedInstall = current;
            }
            if (!ReferenceEquals(old, current) && !current.IsUnavailable)
            {
                _ = current.CheckAsync();
            }
        }
        else if (wasSelected)
        {
            SelectedInstall = Installs.FirstOrDefault();
        }
        OnPropertyChanged(nameof(ServerStateText));
        _ = RefreshCardsAsync(); // the Multiplayer row compares with the server
    }
}
