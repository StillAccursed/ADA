using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Resolution;
using ADA.Core.Settings;
using ADA.Imaging;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>
/// The Mods page: search the local catalogue with filters, tags and sorting, shown as a list, grid
/// or cards; see a mod's details; star it; and install, queue or uninstall it in one game. Every
/// change is queued in click order, resolved against current installed state, and applied when valid.
/// </summary>
public partial class ModsViewModel : ViewModelBase
{
    private const int SCROLL_BATCH_SIZE = 50;

    // Fast enough to feel live, slow enough not to search on every keystroke of a word.
    private static readonly TimeSpan SEARCH_DELAY = TimeSpan.FromMilliseconds(150);

    private readonly ModCatalogStore _store;
    private readonly ModResolutionService _resolution;
    private readonly ModInstallService _installs;
    private readonly GameProfiles _profiles;
    private readonly DashboardViewModel _dashboard;
    private readonly ModLogos _logos;
    private readonly FavouritesStore _favourites;
    private readonly SettingsStore? _settings;
    private readonly ILogger _log;
    private readonly ModsPageChoices? _choices;
    private readonly ModDetailsLoader _details;
    private bool _restoring;

    // Mods waiting to be installed together, per game folder. Kept for the session only.
    private readonly Dictionary<string, HashSet<string>> _queues = new(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, InstalledMod> _installed = new Dictionary<string, InstalledMod>();
    private IReadOnlySet<string> _requested = new HashSet<string>();
    private CancellationTokenSource? _pendingSearch;
    private CancellationTokenSource? _pendingDetails;

    // Searches run off the UI thread and can finish out of order; only the newest may show its results.
    private readonly Lock _resultsLock = new();
    private int _searchGeneration;
    private SynchronizationContext? _viewContext;
    private readonly List<GameInstallViewModel> _watchedGames = [];
    private IReadOnlyList<CatalogMod> _matches = [];
    private bool _endlessScroll;
    private int _modsPageSize;
    private int _currentPage = 1;
    private bool _resetWindowOnSearch = true;
    private string _resultQuery = "";
    private ModFilter _resultFilter;
    private int _resultTagCount;

    // Games the queue changed since it last went idle; offered a server update when it does.
    private readonly HashSet<GameInstallViewModel> _changedInRun = [];
    private readonly ModOperationQueue _operationQueue;

    public ObservableCollection<GameInstallViewModel> Games => _dashboard.Installs;

    public ObservableCollection<ModItemViewModel> Results { get; } = [];

    /// <summary>Shares the gallery's bounded image loader with the description renderer.</summary>
    internal ModDescriptionImageResolver DescriptionImageResolver { get; }

    /// <summary>Shows page navigation when endless scrolling is disabled.</summary>
    public bool IsPaged => !_endlessScroll;

    public int CurrentPage => _currentPage;

    public int TotalPages => (_matches.Count + _modsPageSize - 1) / _modsPageSize;

    public string PageText => TotalPages == 0 ? "No pages" : $"Page {CurrentPage:N0} of {TotalPages:N0}";

    /// <summary>More of the current search snapshot can be appended without another catalogue query.</summary>
    public bool HasMoreResults => _endlessScroll && !IsSearching && Results.Count < _matches.Count;

    /// <summary>Navigation waits for the latest search so an old snapshot cannot be extended.</summary>
    [ObservableProperty]
    internal partial bool IsSearching { get; private set; }

    /// <summary>Changes when the result window is replaced, allowing the view to restore its scroll offset.</summary>
    [ObservableProperty]
    internal partial int ResultsRevision { get; private set; }

    /// <summary>The view's vertical offset, retained while details or another page are shown.</summary>
    internal double ResultsScrollOffset { get; set; }

    /// <summary>Suppresses scroll callbacks while the displayed collection is being changed.</summary>
    internal bool IsUpdatingResults { get; private set; }

    /// <summary>Completes after the latest saved settings have been applied on the view's context.</summary>
    internal Task PendingBrowsingUpdate { get; private set; } = Task.CompletedTask;

    private bool CanPreviousPage => IsPaged && !IsSearching && CurrentPage > 1;

    private bool CanNextPage => IsPaged && !IsSearching && CurrentPage < TotalPages;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    public IReadOnlyList<Option<ModFilter>> Filters => ModListQuery.Filters;

    public IReadOnlyList<Option<ModSort>> Sorts => ModListQuery.Sorts;

    [ObservableProperty]
    public partial Option<ModFilter> Filter { get; set; } = ModListQuery.Filters[0];

    [ObservableProperty]
    public partial Option<ModSort> Sort { get; set; } = ModListQuery.Sorts[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView), nameof(IsGridView), nameof(IsCardView))]
    public partial ModView View { get; set; }

    public bool IsListView { get => View == ModView.List; set { if (value) { View = ModView.List; } } }

    public bool IsGridView { get => View == ModView.Grid; set { if (value) { View = ModView.Grid; } } }

    public bool IsCardView { get => View == ModView.Card; set { if (value) { View = ModView.Card; } } }

    /// <summary>Every tag in the catalogue, most used first; ticking one narrows the list.</summary>
    public ObservableCollection<TagOptionViewModel> Tags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TagsButtonText))]
    public partial int SelectedTagCount { get; set; }

    public string TagsButtonText => SelectedTagCount == 0 ? "Tags" : $"Tags ({SelectedTagCount})";

    /// <summary>A mod needs every ticked tag, rather than any of them.</summary>
    [ObservableProperty]
    public partial bool MatchAllTags { get; set; }

    /// <summary>How many mods wait in the selected game's install queue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueue), nameof(QueueText))]
    [NotifyCanExecuteChangedFor(nameof(InstallQueuedCommand))]
    public partial int QueueCount { get; set; }

    public bool HasQueue => QueueCount > 0;

    public string QueueText => QueueCount == 1 ? "1 mod queued" : $"{QueueCount} mods queued";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(ToggleQueueCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickChangeCommand))]
    public partial GameInstallViewModel? SelectedGame { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(ToggleQueueCommand))]
    public partial ModItemViewModel? SelectedMod { get; set; }

    public bool HasSelection => SelectedMod is not null;

    /// <summary>The list's selection: choosing a mod opens its page, then the list is left unselected.</summary>
    [ObservableProperty]
    public partial ModItemViewModel? ListSelection { get; set; }

    partial void OnListSelectionChanged(ModItemViewModel? value)
    {
        if (value is null)
            return;
        SelectedMod = value;
        ListSelection = null;
    }

    [ObservableProperty]
    public partial string ResultsText { get; set; } = "";

    /// <summary>The operation worker is resolving, applying or refreshing a change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(InstallQueuedCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickChangeCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Operations waiting for their turn, across the captured game targets.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingOperations), nameof(OperationQueueText))]
    public partial int PendingOperationCount { get; private set; }

    public bool HasPendingOperations => PendingOperationCount > 0;

    /// <summary>Explains how many operations are waiting and whether the game must close first.</summary>
    public string OperationQueueText => $"{PendingOperationCount} operation{(PendingOperationCount == 1 ? "" : "s")} waiting"
        + (!IsGameRunning ? "." : SelectedGame?.Install.IsRemote == true ? " until the server stops." : " until Satisfactory closes.");

    /// <summary>The batch button submits work behind the current operation when the worker is busy.</summary>
    public string InstallQueuedButtonText => IsBusy ? "Queue installs" : "Install queued";

    /// <summary>Completes when the current worker drains the queue or pauses for the game; for tests.</summary>
    internal Task PendingOperations => _operationQueue.Pending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(InstallQueuedCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickChangeCommand))]
    [NotifyPropertyChangedFor(nameof(GameRunningText), nameof(OperationQueueText))]
    public partial bool IsGameRunning { get; set; }

    public string? GameRunningText => !IsGameRunning ? null
        : SelectedGame?.Install.IsRemote == true
            ? $"{SelectedGame.Install.Location} is running. Stop the server to install or uninstall mods."
            : "Satisfactory is running. Close it to install or uninstall mods.";

    /// <summary>The selected server can't be reached; why, for the banner above the list. Null otherwise.</summary>
    public string? UnavailableText => SelectedGame is { IsUnavailable: true } game
        ? $"{game.Install.Location} can't be used right now: {game.UnavailableReason}"
        : null;

    /// <summary>Tries the selected server again; the banner's Retry.</summary>
    public IAsyncRelayCommand RetryServerCommand => _dashboard.RetryServerCommand;

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    /// <summary>Outcomes of the current run, retained while subsequent operations execute.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    private bool CanPlan => SelectedGame is { IsUnavailable: false } game && SelectedMod is { } mod
        && !IsGameRunning && !_operationQueue.IsActiveFor(game, mod.Reference);

    private bool CanInstall => CanPlan && SelectedMod is { CanInstallHere: true };

    private bool CanUninstall => CanPlan && SelectedMod is { ShowUninstall: true };

    private bool CanQueue => SelectedGame is not null && SelectedMod is { } mod && (mod.IsQueued || mod.CanBeRequested);

    private bool CanInstallQueued => HasQueue && SelectedGame is { IsUnavailable: false } game && !IsGameRunning
        && CurrentQueue()!.Any(reference => !_operationQueue.Contains(game, reference));

    /// <summary>Completes when the most recent change of layout, sort or filter is saved; for tests.</summary>
    internal Task PendingChoicesSave => _choices?.PendingSave ?? Task.CompletedTask;

    /// <summary>Completes when the most recent search has shown its results; for tests.</summary>
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the open mod page has its versions, dependencies and gallery; for tests.</summary>
    internal Task PendingDetails { get; private set; } = Task.CompletedTask;

    public ModsViewModel(ModCatalogStore store, IModVersionSource versions,
        ModResolutionService resolution, ModInstallService installs, GameProfiles profiles, DashboardViewModel dashboard, ModLogos logos,
        FavouritesStore favourites, ILogger<ModsViewModel> log, SettingsStore? settings = null)
    {
        _store = store;
        _resolution = resolution;
        _installs = installs;
        _profiles = profiles;
        _dashboard = dashboard;
        _logos = logos;
        DescriptionImageResolver = new ModDescriptionImageResolver(logos);
        _favourites = favourites;
        _settings = settings;
        _log = log;
        _choices = settings is null ? null : new ModsPageChoices(settings, log);
        _details = new ModDetailsLoader(store, versions, logos, () => SelectedGame?.Install, () => _installed, log);
        _operationQueue = new ModOperationQueue(resolution, profiles, dashboard, game => game.IsGameRunning, RefreshAfterOperationAsync, log);
        _operationQueue.StateChanged += (_, _) => UpdateOperationState();
        _operationQueue.ProgressChanged += (_, text) => ProgressText = text;
        _operationQueue.BusyChanged += (_, busy) => OnQueueBusyChanged(busy);
        _operationQueue.OutcomesChanged += (_, text) => Notice = text;
        var browsing = settings?.Current ?? AppSettings.Default;
        _endlessScroll = browsing.EndlessScroll;
        _modsPageSize = browsing.ModsPageSize;
        if (settings is not null)
        {
            settings.Changed += OnSettingsChanged;
        }
        RestoreChoices();

        dashboard.Installs.CollectionChanged += OnInstallsChanged;
        WatchGames();
        dashboard.PropertyChanged += OnDashboardChanged;
        dashboard.Catalog.PropertyChanged += OnCatalogChanged;
        SelectedGame = dashboard.SelectedInstall ?? dashboard.Installs.FirstOrDefault();
    }

    /// <summary>
    /// Re-reads installed mods and tags, then searches again. Preserves the current result window
    /// unless resetWindow is true. Called when the page is shown and after operations. Never throws.
    /// </summary>
    public Task RefreshAsync(bool resetWindow = false)
    {
        _viewContext ??= SynchronizationContext.Current;
        var (generation, ct) = BeginSearch(resetWindow);
        PendingSearch = RefreshAndSearchAsync(generation, ct);
        return PendingSearch;
    }

    partial void OnSearchTextChanged(string value) => ScheduleSearch(SEARCH_DELAY);

    partial void OnFilterChanged(Option<ModFilter> value)
    {
        if (_restoring)
            return;
        SaveChoices();
        ScheduleSearch(TimeSpan.Zero);
    }

    partial void OnSortChanged(Option<ModSort> value)
    {
        if (_restoring)
            return;
        SaveChoices();
        ScheduleSearch(TimeSpan.Zero);
    }

    partial void OnViewChanged(ModView value) => SaveChoices();

    /// <summary>The layout, sort and filter last chosen, from settings. Unknown or missing values keep the defaults.</summary>
    private void RestoreChoices()
    {
        if (_choices is null)
        {
            return;
        }

        var (view, sort, filter) = _choices.Restore();
        _restoring = true;
        try
        {
            if (view is { } savedView)
            {
                View = savedView;
            }
            if (sort is { } savedSort && Sorts.FirstOrDefault(o => o.Value == savedSort) is { } sortOption)
            {
                Sort = sortOption;
            }
            if (filter is { } savedFilter && Filters.FirstOrDefault(o => o.Value == savedFilter) is { } filterOption)
            {
                Filter = filterOption;
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void SaveChoices()
    {
        if (_choices is null || _restoring)
        {
            return;
        }
        _choices.Save(View, Sort.Value, Filter.Value);
    }

    partial void OnMatchAllTagsChanged(bool value) => ScheduleSearch(TimeSpan.Zero);

    [RelayCommand]
    private void ClearTags()
    {
        foreach (var tag in Tags)
        {
            tag.IsSelected = false;
        }
    }

    /// <summary>Adds the selected mod to the game's install queue, or takes it out.</summary>
    [RelayCommand(CanExecute = nameof(CanQueue))]
    private void ToggleQueue()
    {
        if (SelectedMod is not { } mod || CurrentQueue() is not { } queue)
        {
            return;
        }

        mod.IsQueued = queue.Add(mod.Reference) || !queue.Remove(mod.Reference);
        QueueCount = queue.Count;
        if (Filter.Value == ModFilter.Queued)
        {
            ScheduleSearch(TimeSpan.Zero);
        }
    }

    /// <summary>Takes a listed mod out of the game's install queue; the Queued filter drops it straight away.</summary>
    [RelayCommand]
    private void RemoveFromQueue(ModItemViewModel? mod)
    {
        if (mod is null || CurrentQueue() is not { } queue || !queue.Remove(mod.Reference))
        {
            return;
        }

        mod.IsQueued = false;
        QueueCount = queue.Count;
        if (Filter.Value == ModFilter.Queued)
        {
            ScheduleSearch(TimeSpan.Zero);
        }
    }

    [RelayCommand]
    private void ClearQueue()
    {
        CurrentQueue()?.Clear();
        QueueCount = 0;
        foreach (var item in Results)
        {
            item.IsQueued = false;
        }

        if (Filter.Value == ModFilter.Queued)
        {
            ScheduleSearch(TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Adds <paramref name="references"/> to the selected install's install queue and shows the Queued filter,
    /// so the user can review them and press Install queued. Installs nothing itself. Used by a save's
    /// "Install missing mods".
    /// </summary>
    internal void QueueForInstall(IReadOnlyCollection<string> references)
    {
        if (CurrentQueue() is not { } queue)
        {
            return;
        }

        queue.UnionWith(references);
        QueueCount = queue.Count;
        SelectedMod = null;
        Filter = Filters.First(o => o.Value == ModFilter.Queued);
    }

    /// <summary>Submits the selected batch as one transaction after earlier operations.</summary>
    [RelayCommand(CanExecute = nameof(CanInstallQueued), AllowConcurrentExecutions = true)]
    private Task InstallQueuedAsync()
    {
        return CanInstallQueued
            ? EnqueueOperation(SelectedGame!, ModOperationKind.Install, CurrentQueue()!.ToArray())
            : Task.CompletedTask;
    }

    private HashSet<string>? CurrentQueue()
    {
        if (SelectedGame is not { } game)
        {
            return null;
        }

        if (!_queues.TryGetValue(game.Install.GamePath, out var queue))
        {
            _queues[game.Install.GamePath] = queue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return queue;
    }

    partial void OnSelectedGameChanged(GameInstallViewModel? value)
    {
        if (!ReferenceEquals(_dashboard.SelectedInstall, value))
        {
            _dashboard.SelectedInstall = value;
        }
        IsGameRunning = value?.IsGameRunning ?? false;
        OnPropertyChanged(nameof(GameRunningText));
        OnPropertyChanged(nameof(OperationQueueText));
        OnPropertyChanged(nameof(UnavailableText));
        QueueCount = CurrentQueue()?.Count ?? 0;
        UpdateOperationState();
        _ = RefreshAsync(resetWindow: true);
    }

    partial void OnSelectedModChanging(ModItemViewModel? value)
    {
        if (SelectedMod is { } old)
            old.PropertyChanged -= OnSelectedModPropertyChanged;
    }

    // Picking another version on the mod page changes what Install does.
    private void OnSelectedModPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModItemViewModel.CanInstallHere))
        {
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedModChanged(ModItemViewModel? value)
    {
        if (value is not null)
            value.PropertyChanged += OnSelectedModPropertyChanged;
        if (value is not null)
        {
            ApplyOperationState(value);
        }
        ReloadDetails(value);
    }

    private void ReloadDetails(ModItemViewModel? mod)
    {
        _pendingDetails?.Cancel();
        if (mod is null)
        {
            return;
        }

        _pendingDetails = new CancellationTokenSource();
        PendingDetails = _details.LoadAsync(mod, _pendingDetails.Token);
    }

    /// <summary>Captures the selected mod, game and version before waiting for earlier changes.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall), AllowConcurrentExecutions = true)]
    private Task InstallAsync()
    {
        return CanInstall
            ? EnqueueOperation(SelectedGame!, ModOperationKind.Install, [SelectedMod!.Reference], SelectedMod.SelectedVersion)
            : Task.CompletedTask;
    }

    /// <summary>Back from a mod page to the list, where the user left it.</summary>
    [RelayCommand]
    private void Back() => SelectedMod = null;

    /// <summary>Captures a removal request without disabling other mods' buttons.</summary>
    [RelayCommand(CanExecute = nameof(CanUninstall), AllowConcurrentExecutions = true)]
    private Task UninstallAsync()
    {
        return CanUninstall
            ? EnqueueOperation(SelectedGame!, ModOperationKind.Uninstall, [SelectedMod!.Reference])
            : Task.CompletedTask;
    }

    /// <summary>
    /// Captures the clicked mod's current action without opening its page. Additional clicks on
    /// other mods enqueue work immediately while the single worker applies earlier requests.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanQuickChange), AllowConcurrentExecutions = true)]
    private Task QuickChangeAsync(ModItemViewModel? mod)
    {
        return CanQuickChange(mod)
            ? EnqueueOperation(SelectedGame!, mod!.IsUninstallAction ? ModOperationKind.Uninstall : ModOperationKind.Install, [mod.Reference])
            : Task.CompletedTask;
    }

    private bool CanQuickChange(ModItemViewModel? mod)
    {
        return mod is not null && SelectedGame is { IsUnavailable: false } game && !IsGameRunning
            && (mod.IsUninstallAction || mod.CanBeRequested)
            && !_operationQueue.IsActiveFor(game, mod.Reference);
    }

    /// <summary>Refuses changes while a profile switch is moving folders; otherwise submits to the queue.</summary>
    private Task EnqueueOperation(GameInstallViewModel game, ModOperationKind kind, IReadOnlyList<string> references, string? version = null)
    {
        if (game.IsSwitchingProfile)
        {
            Notice = "Wait for profile switching to finish before changing mods.";
            return Task.CompletedTask;
        }
        return _operationQueue.EnqueueAsync(game, kind, references, version);
    }

    /// <summary>
    /// Follows the queue's busy state. When a run ends, each local game it changed is offered to
    /// bring a server that shares its profile up to date (the Dashboard asks at most once at a time).
    /// </summary>
    private void OnQueueBusyChanged(bool busy)
    {
        IsBusy = busy;
        if (busy)
        {
            return;
        }
        var changed = _changedInRun.ToList();
        _changedInRun.Clear();
        foreach (var game in changed.Where(g => !g.Install.IsRemote))
        {
            _ = _dashboard.OfferServerUpdateAsync(game);
        }
    }

    /// <summary>Updates the captured game's batch and profile display, then the currently visible game.</summary>
    private async Task RefreshAfterOperationAsync(GameInstallViewModel game)
    {
        _changedInRun.Add(game);
        if (_queues.TryGetValue(game.Install.GamePath, out var changedQueue))
        {
            var installed = await Task.Run(() => _installs.InstallerFor(game.Install).Installed);
            changedQueue.RemoveWhere(installed.ContainsKey);
        }
        await RefreshAsync();
        ReloadDetails(SelectedMod);
        QueueCount = CurrentQueue()?.Count ?? 0;
        await game.CheckAsync();
    }

    /// <summary>Reapplies operation state after queue changes and to replacement search results.</summary>
    private void ApplyOperationState(ModItemViewModel item)
    {
        var status = _operationQueue.StatusFor(SelectedGame, item.Reference);
        item.IsOperationQueueBusy = status.IsQueueBusy;
        item.PendingUninstall = status.PendingUninstall;
        item.OperationPosition = status.Position;
        item.IsOperationRunning = status.IsRunning;
    }

    private void UpdateOperationState()
    {
        PendingOperationCount = _operationQueue.PendingCount;
        foreach (var game in Games)
        {
            game.HasModOperations = _operationQueue.HasOperationsFor(game);
        }
        lock (_resultsLock)
        {
            foreach (var item in Results)
            {
                ApplyOperationState(item);
            }
            if (SelectedMod is { } open)
            {
                ApplyOperationState(open);
            }
        }
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        QuickChangeCommand.NotifyCanExecuteChanged();
        InstallQueuedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(InstallQueuedButtonText));
    }

    partial void OnIsGameRunningChanged(bool value)
    {
        UpdateOperationState();
        if (!value)
        {
            _operationQueue.Resume();
        }
    }

    /// <summary>Appends one batch from the current snapshot; obsolete searches cannot be extended.</summary>
    [RelayCommand(CanExecute = nameof(HasMoreResults))]
    private void LoadMore()
    {
        lock (_resultsLock)
        {
            if (!HasMoreResults || IsUpdatingResults)
            {
                return;
            }
            AppendResults(Results.Count, SCROLL_BATCH_SIZE);
            UpdateBrowsingState();
            UpdateResultsText();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviousPage))]
    private void PreviousPage()
    {
        if (CanPreviousPage)
        {
            ShowPage(CurrentPage - 1);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    private void NextPage()
    {
        if (CanNextPage)
        {
            ShowPage(CurrentPage + 1);
        }
    }

    private void ShowPage(int page)
    {
        lock (_resultsLock)
        {
            _currentPage = page;
            ResultsScrollOffset = 0;
            ReplaceResults(_modsPageSize);
            UpdateBrowsingState();
            UpdateResultsText();
        }
    }

    partial void OnIsSearchingChanged(bool value) => UpdateBrowsingState();

    private void UpdateBrowsingState()
    {
        OnPropertyChanged(nameof(IsPaged));
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(HasMoreResults));
        LoadMoreCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Settings are saved on a worker thread. Apply the latest values on the context captured
    /// when the page is refreshed, including saves which finish after navigating back to Mods.
    /// </summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingBrowsingUpdate = completion.Task;
        void Apply()
        {
            try
            {
                ApplyBrowsingSettings();
            }
            catch (Exception error)
            {
                _log.LogError(error, "Applying Mods browsing settings failed");
            }
            finally
            {
                completion.SetResult();
            }
        }

        if (_viewContext is { } context && !ReferenceEquals(context, SynchronizationContext.Current))
        {
            context.Post(_ => Apply(), null);
        }
        else
        {
            Apply();
        }
    }

    private void ApplyBrowsingSettings()
    {
        var saved = _settings!.Current;
        lock (_resultsLock)
        {
            var reset = _endlessScroll != saved.EndlessScroll
                || (!saved.EndlessScroll && _modsPageSize != saved.ModsPageSize);
            _endlessScroll = saved.EndlessScroll;
            _modsPageSize = saved.ModsPageSize;
            if (!reset)
            {
                return;
            }
            if (IsSearching)
            {
                _resetWindowOnSearch = true;
            }
            else
            {
                _currentPage = 1;
                ResultsScrollOffset = 0;
                ReplaceResults(_endlessScroll ? SCROLL_BATCH_SIZE : _modsPageSize);
                UpdateResultsText();
            }
            UpdateBrowsingState();
        }
    }

    /// <summary>Invalidates results immediately, including during the search-text debounce.</summary>
    private (int Generation, CancellationToken Token) BeginSearch(bool resetWindow)
    {
        lock (_resultsLock)
        {
            _pendingSearch?.Cancel();
            _pendingSearch?.Dispose();
            _pendingSearch = new CancellationTokenSource();
            var generation = Interlocked.Increment(ref _searchGeneration);
            _resetWindowOnSearch |= resetWindow;
            IsSearching = true;
            return (generation, _pendingSearch.Token);
        }
    }

    private void ScheduleSearch(TimeSpan delay)
    {
        var (generation, ct) = BeginSearch(resetWindow: true);
        PendingSearch = DelayThenSearchAsync(delay, generation, ct);
    }

    private async Task RefreshAndSearchAsync(int generation, CancellationToken ct)
    {
        await LoadInstalledAsync();
        if (ct.IsCancellationRequested)
        {
            return;
        }
        await LoadTagsAsync();
        await SearchAsync(generation, ct);
    }

    private async Task DelayThenSearchAsync(TimeSpan delay, int generation, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await SearchAsync(generation, ct);
    }

    private async Task SearchAsync(int generation, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var query = SearchText;
            var filter = Filter.Value;
            var sort = Sort.Value;
            var tags = Tags.Where(t => t.IsSelected).Select(t => t.Name).ToList();
            var matchAll = MatchAllTags;
            var game = SelectedGame?.Install;
            var context = new ModListContext(
                game?.Build is { } build ? (build.Changelist, ModInstallService.TargetFor(game)) : null,
                _installed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                _requested,
                _favourites.All,
                CurrentQueue()?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? []);

            // Filters and sorts apply to every match, so the top results are the right ones.
            var mods = await Task.Run(() => ModListQuery.Apply(
                _store.Search(query, int.MaxValue), filter, tags, matchAll, sort, context).ToList(), ct);

            lock (_resultsLock)
            {
                if (ct.IsCancellationRequested || generation != Volatile.Read(ref _searchGeneration))
                {
                    return;
                }

                var shown = _resetWindowOnSearch ? SCROLL_BATCH_SIZE : Math.Max(SCROLL_BATCH_SIZE, Results.Count);
                if (_resetWindowOnSearch)
                {
                    _currentPage = 1;
                    ResultsScrollOffset = 0;
                }
                _matches = mods;
                _resultQuery = query;
                _resultFilter = filter;
                _resultTagCount = tags.Count;
                var page = Math.Clamp(_currentPage, 1, Math.Max(1, TotalPages));
                if (page != _currentPage)
                {
                    ResultsScrollOffset = 0;
                }
                _currentPage = page;
                _resetWindowOnSearch = false;
                ReplaceResults(_endlessScroll ? shown : _modsPageSize);
                // An open mod page keeps its mod even if the new results leave it out (e.g. just uninstalled
                // while filtering to Installed); only its state is brought up to date.
                if (SelectedMod is { } open)
                {
                    ApplyInstalled(open);
                    open.IsFavourite = context.Favourites.Contains(open.Reference);
                    open.IsQueued = context.Queued.Contains(open.Reference);
                }
                IsSearching = false;
                UpdateBrowsingState();
                UpdateResultsText();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        catch (Exception e)
        {
            _log.LogError(e, "Searching the catalogue failed");
            lock (_resultsLock)
            {
                if (generation == Volatile.Read(ref _searchGeneration))
                {
                    _matches = [];
                    ResultsScrollOffset = 0;
                    ReplaceResults(0);
                    IsSearching = false;
                    UpdateBrowsingState();
                    ResultsText = $"Couldn't search the mod list: {e.Message}";
                }
            }
        }
    }

    /// <summary>Replaces the visible window; appending batches leaves existing mod items intact.</summary>
    private void ReplaceResults(int count)
    {
        IsUpdatingResults = true;
        try
        {
            Results.Clear();
            AppendResults(_endlessScroll ? 0 : (CurrentPage - 1) * _modsPageSize, count);
        }
        finally
        {
            IsUpdatingResults = false;
        }
        ResultsRevision++;
    }

    private void AppendResults(int start, int count)
    {
        var favourites = _favourites.All;
        var queued = CurrentQueue();
        IsUpdatingResults = true;
        try
        {
            foreach (var mod in _matches.Skip(start).Take(count))
            {
                var item = new ModItemViewModel(mod, SetFavourite)
                {
                    IsFavourite = favourites.Contains(mod.Reference),
                    IsQueued = queued?.Contains(mod.Reference) == true,
                };
                ApplyInstalled(item);
                Results.Add(item);
                _ = LoadLogoAsync(item);
            }
        }
        finally
        {
            IsUpdatingResults = false;
        }
    }

    private void UpdateResultsText() => ResultsText = Describe(
        _matches.Count, Results.Count, _resultFilter, _resultQuery, _resultTagCount);

    private string Describe(int total, int shown, ModFilter filter, string query, int tagCount)
    {
        if (total == 0)
        {
            if (_store.Count() == 0)
            {
                return "The mod list hasn't been downloaded yet; it updates when ADA starts.";
            }
            return (filter, query.Trim().Length, tagCount) switch
            {
                (ModFilter.Installed, 0, 0) => "No mods installed by ADA in this game yet.",
                (ModFilter.Favourite, 0, 0) => "No favourites yet. Star a mod to add it here.",
                (ModFilter.Queued, 0, 0) => "Nothing queued. Select a mod and choose Queue.",
                (ModFilter.Compatible, _, _) when SelectedGame?.Install.Build is null => "This game's build is unknown, so compatibility can't be checked.",
                (_, > 0, _) => $"No mods match \"{query.Trim()}\" with these filters.",
                _ => "No mods match these filters.",
            };
        }
        if (IsPaged)
        {
            var first = (CurrentPage - 1) * _modsPageSize + 1;
            return $"{first:N0}–{first + shown - 1:N0} of {total:N0} mods";
        }
        return shown < total
            ? $"Showing {shown:N0} of {total:N0} mods. Scroll for more."
            : total == 1 ? "1 mod" : $"{total:N0} mods";
    }

    private void SetFavourite(ModItemViewModel item, bool favourite)
    {
        try
        {
            _favourites.Set(item.Reference, favourite);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Saving favourites failed");
            Notice = $"Couldn't save favourites: {e.Message}";
        }
        if (Filter.Value == ModFilter.Favourite)
        {
            ScheduleSearch(TimeSpan.Zero);
        }
    }

    private async Task LoadLogoAsync(ModItemViewModel item) => item.Logo ??= await _logos.GetAsync(item.Mod.LogoUrl);

    private async Task LoadTagsAsync()
    {
        if (Tags.Count > 0)
        {
            return;
        }

        try
        {
            foreach (var (name, count) in await Task.Run(_store.Tags))
            {
                var tag = new TagOptionViewModel(name, count);
                tag.PropertyChanged += OnTagChanged;
                Tags.Add(tag);
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't read tags");
        }
    }

    private void OnTagChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TagOptionViewModel.IsSelected))
        {
            return;
        }

        SelectedTagCount = Tags.Count(t => t.IsSelected);
        ScheduleSearch(TimeSpan.Zero);
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
            var state = await Task.Run(() =>
            {
                var installer = _installs.InstallerFor(game.Install);
                return (installer.Installed, installer.Requested);
            });
            if (!ReferenceEquals(game, SelectedGame))
            {
                return;
            }
            (_installed, _requested) = state;
        }
        catch (Exception e)
        {
            _log.LogError(e, "Reading installed mods in {Game} failed", game.Install.GamePath);
            Notice = $"Couldn't read which mods are installed: {e.Message}";
        }
        foreach (var item in Results)
        {
            ApplyInstalled(item);
        }

        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        ToggleQueueCommand.NotifyCanExecuteChanged();
        QuickChangeCommand.NotifyCanExecuteChanged();
    }

    private void ApplyInstalled(ModItemViewModel item)
    {
        item.InstalledVersion = _installed.GetValueOrDefault(item.Reference)?.Version;
        item.IsDependencyOnly = item.IsInstalled && !_requested.Contains(item.Reference);
        item.NoBuildHere = NoBuildHere(item.Mod);
        ApplyOperationState(item);
    }

    /// <summary>
    /// Why the selected game can't take this mod at all, when none of its newest builds are for the
    /// game's platform. Null when it can, or when the mod's builds aren't known.
    /// </summary>
    private NoBuildNote? NoBuildHere(CatalogMod mod)
    {
        if (SelectedGame is not { } game || mod.Latest is not { Count: > 0 } latest)
        {
            return null;
        }

        var platform = ModInstallService.TargetFor(game.Install);
        if (latest.Any(l => l.Targets.Contains(platform)))
        {
            return null;
        }
        return platform == TargetPlatform.Windows
            ? new NoBuildNote("Server only", "No build for the game. It only runs on dedicated servers.")
            : new NoBuildNote("No server build",
                $"No {(platform == TargetPlatform.LinuxServer ? "Linux" : "Windows")} server build. Mods without a server build are usually client-only, and the server doesn't need them; install it in your game instead.");
    }

    private void OnInstallsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchGames();
        if (SelectedGame is null || !Games.Contains(SelectedGame))
        {
            SelectedGame = Games.FirstOrDefault();
        }
    }

    /// <summary>Follows every install's own state: the selected one's running and reachability, and any stopping game resuming the queue.</summary>
    private void WatchGames()
    {
        foreach (var game in _watchedGames)
        {
            game.PropertyChanged -= OnGameChanged;
        }
        _watchedGames.Clear();
        _watchedGames.AddRange(Games);
        foreach (var game in _watchedGames)
        {
            game.PropertyChanged += OnGameChanged;
        }
    }

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GameInstallViewModel game)
        {
            return;
        }
        if (e.PropertyName == nameof(GameInstallViewModel.IsGameRunning))
        {
            if (ReferenceEquals(game, SelectedGame))
            {
                IsGameRunning = game.IsGameRunning;
            }
            if (!game.IsGameRunning)
            {
                _operationQueue.Resume();
            }
        }
        else if (e.PropertyName == nameof(GameInstallViewModel.UnavailableReason) && ReferenceEquals(game, SelectedGame))
        {
            OnPropertyChanged(nameof(UnavailableText));
            UpdateOperationState();
            InstallCommand.NotifyCanExecuteChanged();
            UninstallCommand.NotifyCanExecuteChanged();
            QuickChangeCommand.NotifyCanExecuteChanged();
            InstallQueuedCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.SelectedInstall)
            && !ReferenceEquals(SelectedGame, _dashboard.SelectedInstall))
        {
            SelectedGame = _dashboard.SelectedInstall;
        }
    }

    // The first launch downloads the mod list after the page may already be open.
    private void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogStatusViewModel.IsChecking) && !_dashboard.Catalog.IsChecking)
        {
            if (Tags.Count == 0)
            {
                _ = LoadTagsAsync();
            }

            ScheduleSearch(TimeSpan.Zero);
        }
    }
}

