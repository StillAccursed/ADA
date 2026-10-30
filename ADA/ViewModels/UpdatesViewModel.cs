using System.Collections.ObjectModel;
using System.ComponentModel;
using ADA.Core.Catalog;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Semver;

namespace ADA.ViewModels;

/// <summary>
/// The "Check for Updates" window: every installed mod with a newer version that works in its game,
/// each with its changelog and its own Update button, plus Update all. Nothing updates on its own.
/// </summary>
public partial class UpdatesViewModel : ViewModelBase
{
    // A click right after a refresh needn't ask ficsit.app again.
    private static readonly TimeSpan FRESH_ENOUGH = TimeSpan.FromMinutes(5);

    private readonly DashboardViewModel _dashboard;

    // Local games this window updated; offered a server update when it closes.
    private readonly HashSet<GameInstallViewModel> _updated = [];
    private readonly ModResolutionService _resolution;
    private readonly GameProfiles _profiles;
    private readonly ModCatalogStore _store;
    private readonly ModChangelogs _changelogs;
    private readonly ILogger _log;
    private readonly Dictionary<GameInstallViewModel, ModChangePlan> _plans = [];

    public ObservableCollection<ModUpdateItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdates))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    public partial int UpdateCount { get; set; }

    public bool HasUpdates => UpdateCount > 0;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Checking installed mods for updates…";

    /// <summary>What the last update did, or why a game couldn't be checked.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>A game in the list is running: Satisfactory here, or a connected server.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    [NotifyPropertyChangedFor(nameof(GameRunningText))]
    public partial bool IsGameRunning { get; set; }

    /// <summary>What to stop before updating; names the server when that's what's running.</summary>
    public string GameRunningText => _dashboard.Installs.FirstOrDefault(g => g.IsGameRunning) is { Install.IsRemote: true } server
        ? $"Stop {server.Install.Location} to update its mods."
        : "Close Satisfactory to update mods.";

    internal bool CanUpdate => !IsBusy && !IsGameRunning;

    private bool CanUpdateAll => HasUpdates && CanUpdate;

    public UpdatesViewModel(DashboardViewModel dashboard, ModResolutionService resolution, GameProfiles profiles, ModCatalogStore store,
        IModVersionSource versions, ILogger<UpdatesViewModel> log)
    {
        _dashboard = dashboard;
        _resolution = resolution;
        _profiles = profiles;
        _store = store;
        _changelogs = new ModChangelogs(versions);
        _log = log;
        dashboard.PropertyChanged += OnDashboardChanged;
        foreach (var game in dashboard.Installs)
        {
            game.PropertyChanged += OnGameChanged;
        }
        IsGameRunning = dashboard.Installs.Any(g => g.IsGameRunning);
    }

    /// <summary>Makes sure the catalogue knows about new releases, then lists updates for every game. Never throws.</summary>
    public async Task CheckAsync()
    {
        IsBusy = true;
        StatusText = "Checking installed mods for updates…";
        try
        {
            var catalog = _dashboard.Catalog;
            if (!catalog.IsChecking && (catalog.LastChecked is not { } at || DateTimeOffset.UtcNow - at > FRESH_ENOUGH))
            {
                await catalog.RefreshCommand.ExecuteAsync(null);
            }

            await ListAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync()
    {
        var updated = new List<string>();
        await ApplyAsync(async progress =>
        {
            foreach (var (game, plan) in _plans.ToList())
            {
                var result = await _resolution.ApplyAsync(game.Install, plan, progress);
                _updated.Add(game);
                await _profiles.RecordInstalledAsync(game.Install);
                updated.AddRange(result.Installed.Select(m => $"{m.Reference} {m.Version}"));
            }
            return $"Updated {string.Join(", ", updated)}.";
        });
    }

    internal Task UpdateAsync(ModUpdateItemViewModel item) => ApplyAsync(async progress =>
    {
        var plan = await _resolution.PlanUpdateAsync(item.Game.Install, item.Reference);
        if (!plan.Succeeded)
        {
            return string.Join(" ", plan.Problems);
        }

        var result = await _resolution.ApplyAsync(item.Game.Install, plan, progress);
        _updated.Add(item.Game);
        await _profiles.RecordInstalledAsync(item.Game.Install);
        return $"Updated {string.Join(", ", result.Installed.Select(m => $"{m.Reference} {m.Version}"))}.";
    });

    /// <summary>Every release after the installed version up to the new one, newest first.</summary>
    internal Task<string> ChangelogAsync(ModUpdateItemViewModel item) => _changelogs.ForRangeAsync(item.Reference, item.From, item.To);

    private async Task ApplyAsync(Func<IProgress<ModInstallProgress>, Task<string>> apply)
    {
        IsBusy = true;
        Notice = null;
        try
        {
            Notice = await apply(new Progress<ModInstallProgress>(p => ProgressText = PlanText.Progress(p)));
        }
        catch (Exception e)
        {
            // Offline, the game started, a failed install (already rolled back): shown, never thrown.
            if (e is not GameRunningException)
            {
                _log.LogError(e, "Updating mods failed");
            }

            Notice = e is GameRunningException ? e.Message : $"Nothing was changed: {e.Message}";
        }
        finally
        {
            ProgressText = null;
        }

        try
        {
            await ListAsync();
            foreach (var game in _dashboard.Installs)
            {
                _ = game.CheckAsync(); // the Dashboard's cards and arrow
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ListAsync()
    {
        Items.Clear();
        _plans.Clear();
        var problems = new List<string>();
        var games = _dashboard.Installs.Where(g => g.Install.Build is not null).ToList();
        foreach (var game in games)
        {
            try
            {
                var plan = await _resolution.PlanUpdateAsync(game.Install);
                if (!plan.Succeeded)
                {
                    problems.Add($"Couldn't check {game.Install.DisplayName}: {string.Join(" ", plan.Problems)}");
                    continue;
                }

                var updates = plan.Install.Where(i => i.IsUpdate).ToList();
                if (updates.Count == 0)
                {
                    continue;
                }

                _plans[game] = plan;
                foreach (var update in updates)
                {
                    var name = await Task.Run(() => _store.Get(update.Reference)?.Name) ?? update.Reference;
                    Items.Add(new ModUpdateItemViewModel(this, game, update.Reference, name, update.InstalledVersion!, update.Version,
                        games.Count > 1));
                }
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Couldn't check {Game} for updates", game.Install.GamePath);
                problems.Add($"Couldn't check {game.Install.DisplayName}: {e.Message}");
            }
        }

        UpdateCount = Items.Count;
        StatusText = (Items.Count, games.Count) switch
        {
            (_, 0) => "No game with mods to check.",
            (0, _) => "All installed mods are up to date.",
            (1, _) => "1 mod has an update.",
            var (n, _) => $"{n} mods have updates.",
        };
        if (problems.Count > 0)
        {
            Notice = string.Join("\n", problems.Prepend(Notice).OfType<string>());
        }
    }

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.IsGameRunning))
        {
            IsGameRunning = _dashboard.Installs.Any(g => g.IsGameRunning);
        }
    }

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameInstallViewModel.IsGameRunning))
        {
            IsGameRunning = _dashboard.Installs.Any(g => g.IsGameRunning);
        }
    }

    partial void OnIsBusyChanged(bool value) => NotifyItems();

    partial void OnIsGameRunningChanged(bool value) => NotifyItems();

    private void NotifyItems()
    {
        foreach (var item in Items)
        {
            item.UpdateCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Stops listening to the Dashboard once the window closes.</summary>
    /// <summary>
    /// Stops following the Dashboard. Each local game this window updated is then offered to bring a
    /// server that shares its profile up to date; asked on close so the question isn't hidden behind
    /// this window, and once rather than after every update.
    /// </summary>
    public void Close()
    {
        _dashboard.PropertyChanged -= OnDashboardChanged;
        foreach (var game in _dashboard.Installs)
        {
            game.PropertyChanged -= OnGameChanged;
        }
        foreach (var game in _updated.Where(g => !g.Install.IsRemote))
        {
            _ = _dashboard.OfferServerUpdateAsync(game);
        }
        _updated.Clear();
    }
}

/// <summary>One mod with an update, in the updates window.</summary>
public partial class ModUpdateItemViewModel(UpdatesViewModel owner, GameInstallViewModel game, string reference, string name,
    string from, string to, bool showGame) : ViewModelBase
{
    public GameInstallViewModel Game { get; } = game;

    public string Reference { get; } = reference;

    public string Name { get; } = name;

    public string From { get; } = from;

    public string To { get; } = to;

    public string VersionText => $"{From} → {To}";

    /// <summary>Which game, when more than one has updates.</summary>
    public string? GameText { get; } = showGame ? game.Install.DisplayName : null;

    public bool ShowGame => GameText is not null;

    [ObservableProperty]
    public partial bool ShowChangelog { get; set; }

    [ObservableProperty]
    public partial string? Changelog { get; set; }

    [RelayCommand]
    private async Task ToggleChangelogAsync()
    {
        ShowChangelog = !ShowChangelog;
        if (!ShowChangelog || Changelog is not null)
        {
            return;
        }

        Changelog = "Loading changelog…";
        try
        {
            Changelog = await owner.ChangelogAsync(this);
        }
        catch (Exception e)
        {
            Changelog = $"Couldn't load the changelog: {e.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateAsync() => owner.UpdateAsync(this);

    private bool CanUpdate => owner.CanUpdate;
}
