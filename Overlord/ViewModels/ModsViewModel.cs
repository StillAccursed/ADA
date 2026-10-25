using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Overlord.Core.Catalog;
using Overlord.Core.Games;
using Overlord.Core.Resolution;
using Overlord.Imaging;
using Overlord.Infrastructure.Catalog;
using Overlord.Infrastructure.Installing;

namespace Overlord.ViewModels;

/// <summary>
/// The Mods page: search the local catalogue, see a mod's details, and install or uninstall it in
/// one game. Every change is planned through the resolver and shown for confirmation first.
/// </summary>
public partial class ModsViewModel : ViewModelBase
{
    private const int ResultLimit = 200;

    // Fast enough to feel live, slow enough not to search on every keystroke of a word.
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);

    private readonly ModCatalogStore _store;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<CatalogVersion>>> _getVersions;
    private readonly ModResolutionService _resolution;
    private readonly ModInstallService _installs;
    private readonly DashboardViewModel _dashboard;
    private readonly ModLogos _logos;
    private readonly ILogger _log;

    private IReadOnlyDictionary<string, InstalledMod> _installed = new Dictionary<string, InstalledMod>();
    private IReadOnlySet<string> _requested = new HashSet<string>();
    private CancellationTokenSource? _pendingSearch;
    private CancellationTokenSource? _pendingDetails;

    // Searches run off the UI thread and can finish out of order; only the newest may show its results.
    private readonly Lock _resultsLock = new();
    private int _searchGeneration;

    public ModsViewModel(ModCatalogStore store, Func<string, CancellationToken, Task<IReadOnlyList<CatalogVersion>>> getVersions,
        ModResolutionService resolution, ModInstallService installs, DashboardViewModel dashboard, ModLogos logos,
        ILogger<ModsViewModel> log)
    {
        _store = store;
        _getVersions = getVersions;
        _resolution = resolution;
        _installs = installs;
        _dashboard = dashboard;
        _logos = logos;
        _log = log;

        dashboard.Installs.CollectionChanged += OnInstallsChanged;
        dashboard.PropertyChanged += OnDashboardChanged;
        dashboard.Catalog.PropertyChanged += OnCatalogChanged;
        SelectedGame = dashboard.Installs.FirstOrDefault();
    }

    public ObservableCollection<GameInstallViewModel> Games => _dashboard.Installs;

    public ObservableCollection<ModItemViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool InstalledOnly { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand))]
    public partial GameInstallViewModel? SelectedGame { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand))]
    public partial ModItemViewModel? SelectedMod { get; set; }

    public bool HasSelection => SelectedMod is not null;

    [ObservableProperty]
    public partial string ResultsText { get; set; } = "";

    /// <summary>Planning or applying a change; the buttons wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(ConfirmCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>A planned change waiting for the user to confirm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingPlan))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(ConfirmCommand))]
    public partial ModChangePlan? PendingPlan { get; set; }

    public bool HasPendingPlan => PendingPlan is not null;

    [ObservableProperty]
    public partial string PlanTitle { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> PlanLines { get; set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(GameRunningText))]
    public partial bool IsGameRunning { get; set; }

    public string? GameRunningText => IsGameRunning
        ? "Satisfactory is running. Close it to install or uninstall mods."
        : null;

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    /// <summary>The outcome of the last change, or why it couldn't be made.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    private bool CanPlan => SelectedGame is not null && SelectedMod is not null && !IsBusy && PendingPlan is null && !IsGameRunning;

    private bool CanInstall => CanPlan && SelectedMod is { CanBeRequested: true };

    private bool CanUninstall => CanPlan && SelectedMod is { IsInstalled: true };

    private bool CanConfirm => PendingPlan is not null && !IsBusy && !IsGameRunning;

    /// <summary>Completes when the most recent search has shown its results; for tests.</summary>
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;

    /// <summary>Re-reads what is installed and searches again. Called whenever the page is shown. Never throws.</summary>
    public async Task RefreshAsync()
    {
        await LoadInstalledAsync();
        await SearchAsync(CancellationToken.None);
    }

    partial void OnSearchTextChanged(string value) => QueueSearch(SearchDelay);

    partial void OnInstalledOnlyChanged(bool value) => QueueSearch(TimeSpan.Zero);

    partial void OnSelectedGameChanged(GameInstallViewModel? value)
    {
        PendingPlan = null;
        Notice = null;
        _ = RefreshAsync();
    }

    partial void OnSelectedModChanged(ModItemViewModel? value)
    {
        PendingPlan = null;
        _pendingDetails?.Cancel();
        if (value is null)
            return;
        _pendingDetails = new CancellationTokenSource();
        _ = LoadDetailsAsync(value, _pendingDetails.Token);
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() =>
        PlanAsync(game => _resolution.PlanInstallAsync(game, SelectedMod!.Reference), $"Install {SelectedMod!.Name}?");

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private Task UninstallAsync() =>
        PlanAsync(game => _resolution.PlanUninstallAsync(game, SelectedMod!.Reference), $"Uninstall {SelectedMod!.Name}?");

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (PendingPlan is not { } plan || SelectedGame is not { } game)
            return;

        IsBusy = true;
        Notice = null;
        try
        {
            var progress = new Progress<ModInstallProgress>(p => ProgressText = p.Stage switch
            {
                InstallStage.Downloading when p.Total is > 0 => $"Downloading {p.Reference}: {100 * p.Done / p.Total.Value}%",
                InstallStage.Downloading => $"Downloading {p.Reference}…",
                InstallStage.Extracting => $"Installing {p.Reference}: {p.Done} of {p.Total} files",
                _ => $"Finishing {p.Reference}…",
            });
            var result = await _resolution.ApplyAsync(game.Install, plan, progress);
            Notice = Describe(result);
        }
        catch (Exception e)
        {
            // Offline, the game started, a failed install (already rolled back): shown, never thrown.
            if (e is not GameRunningException)
                _log.LogError(e, "Changing mods in {Game} failed", game.Install.GamePath);
            Notice = e is GameRunningException ? e.Message : $"Nothing was changed: {e.Message}";
        }
        finally
        {
            PendingPlan = null;
            ProgressText = null;
            IsBusy = false;
        }

        // Show the new state here and on the Dashboard.
        await RefreshAsync();
        _ = game.CheckAsync();
    }

    [RelayCommand]
    private void Cancel() => PendingPlan = null;

    private async Task PlanAsync(Func<GameInstall, Task<ModChangePlan>> plan, string title)
    {
        if (SelectedGame is not { } game)
            return;

        IsBusy = true;
        Notice = null;
        ProgressText = "Working out what needs to change…";
        try
        {
            var result = await plan(game.Install);
            if (!result.Succeeded)
                Notice = string.Join("\n", result.Problems);
            else if (result.ChangesNothing)
                Notice = "Already installed; nothing to change.";
            else
            {
                PlanTitle = title;
                PlanLines = Lines(result);
                PendingPlan = result;
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Planning a change in {Game} failed", game.Install.GamePath);
            Notice = $"Couldn't work out the change: {e.Message}";
        }
        finally
        {
            ProgressText = null;
            IsBusy = false;
        }
    }

    private void QueueSearch(TimeSpan delay)
    {
        _pendingSearch?.Cancel();
        _pendingSearch = new CancellationTokenSource();
        PendingSearch = DelayThenSearchAsync(delay, _pendingSearch.Token);
    }

    private async Task DelayThenSearchAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await SearchAsync(ct);
    }

    private async Task SearchAsync(CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _searchGeneration);
        try
        {
            var query = SearchText;
            var installedOnly = InstalledOnly;
            var installed = _installed;
            var mods = await Task.Run(() => installedOnly
                // The catalogue is a few thousand mods; filtering a full search is simpler than a second query.
                ? _store.Search(query, int.MaxValue).Where(m => installed.ContainsKey(m.Reference)).ToList()
                : _store.Search(query, ResultLimit), ct);
            lock (_resultsLock)
            {
                if (ct.IsCancellationRequested || generation != Volatile.Read(ref _searchGeneration))
                    return;

                var selected = SelectedMod?.Reference;
                Results.Clear();
                foreach (var mod in mods)
                {
                    var item = new ModItemViewModel(mod);
                    ApplyInstalled(item);
                    Results.Add(item);
                }
                SelectedMod = Results.FirstOrDefault(r => string.Equals(r.Reference, selected, StringComparison.OrdinalIgnoreCase));
            }
            ResultsText = (mods.Count, installedOnly, query.Trim().Length) switch
            {
                (0, true, 0) => "No mods installed by Overlord in this game yet.",
                (0, _, 0) => "The mod list hasn't been downloaded yet. Check for updates on the Dashboard.",
                (0, _, _) => $"No mods match \"{query.Trim()}\".",
                (ResultLimit, false, _) => $"Showing the top {ResultLimit} matches.",
                (1, _, _) => "1 mod",
                var (n, _, _) => $"{n} mods",
            };
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        catch (Exception e)
        {
            _log.LogError(e, "Searching the catalogue failed");
            ResultsText = $"Couldn't search the mod list: {e.Message}";
        }
    }

    private async Task LoadInstalledAsync()
    {
        if (SelectedGame is not { } game)
        {
            _installed = new Dictionary<string, InstalledMod>();
            _requested = new HashSet<string>();
            return;
        }

        try
        {
            (_installed, _requested) = await Task.Run(() =>
            {
                var installer = _installs.InstallerFor(game.Install);
                return (installer.Installed, installer.Requested);
            });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Reading installed mods in {Game} failed", game.Install.GamePath);
            Notice = $"Couldn't read which mods are installed: {e.Message}";
        }
        foreach (var item in Results)
            ApplyInstalled(item);
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    private void ApplyInstalled(ModItemViewModel item)
    {
        item.InstalledVersion = _installed.GetValueOrDefault(item.Reference)?.Version;
        item.IsDependencyOnly = item.IsInstalled && !_requested.Contains(item.Reference);
    }

    private async Task LoadDetailsAsync(ModItemViewModel item, CancellationToken ct)
    {
        var logo = _logos.GetAsync(item.Mod.LogoUrl);
        try
        {
            item.LatestText = await LatestForGameAsync(item.Reference, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't look up versions of {Mod}", item.Reference);
            item.LatestText = $"Couldn't look up versions: {e.Message}";
        }
        item.Logo ??= await logo;
    }

    private async Task<string?> LatestForGameAsync(string reference, CancellationToken ct)
    {
        if (SelectedGame?.Install is not { Build: { } build } game)
            return null;

        var platform = ModInstallService.TargetFor(game);
        var versions = await _getVersions(reference, ct);
        var latest = versions
            .Where(v => v.TargetFor(platform) is not null
                        && GameVersionRange.TryParse(v.GameVersionRange, out var range) && range.Contains(build.Changelist))
            .OrderBy(v => v.Stability)
            .ThenByDescending(v => v.CreatedAt)
            .FirstOrDefault();
        return latest is null
            ? $"No version supports game build {build.Changelist} yet."
            : $"Latest for this game: {latest.Version}{(latest.Stability == VersionStability.Release ? "" : $" ({latest.Stability})")}";
    }

    internal static IReadOnlyList<string> Lines(ModChangePlan plan)
    {
        var lines = new List<string>();
        foreach (var install in plan.Install)
        {
            var why = install.Mod.RequiredBy.Contains("requested")
                ? ""
                : $" (needed by {string.Join(", ", install.Mod.RequiredBy)})";
            lines.Add(install.InstalledVersion is { } from
                ? $"Update {install.Reference} {from} → {install.Version}{why}"
                : $"Install {install.Reference} {install.Version}{why}");
        }
        lines.AddRange(plan.Uninstall.Select(r => $"Remove {r}"));
        if (plan.Install.Count == 0 && plan.Uninstall.Count == 0)
            lines.Add("Keep it installed even when no other mod needs it");
        if (plan.DownloadBytes > 0)
            lines.Add($"Download up to {SettingsViewModel.FormatSize(plan.DownloadBytes)}");
        return lines;
    }

    private static string Describe(TransactionResult result)
    {
        var parts = new List<string>();
        if (result.Installed.Count > 0)
            parts.Add($"Installed {string.Join(", ", result.Installed.Select(m => $"{m.Reference} {m.Version}"))}.");
        if (result.Uninstalled.Count > 0)
            parts.Add($"Removed {string.Join(", ", result.Uninstalled)}.");
        return string.Join(" ", parts);
    }

    private void OnInstallsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedGame is null || !Games.Contains(SelectedGame))
            SelectedGame = Games.FirstOrDefault();
    }

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.IsGameRunning))
            IsGameRunning = _dashboard.IsGameRunning;
    }

    // The first launch downloads the mod list after the page may already be open.
    private void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogStatusViewModel.IsChecking) && !_dashboard.Catalog.IsChecking)
            QueueSearch(TimeSpan.Zero);
    }
}
