using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Resolution;
using ADA.Core.Settings;
using ADA.Imaging;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;

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
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);

    private readonly ModCatalogStore _store;
    private readonly Func<string, bool, CancellationToken, Task<IReadOnlyList<CatalogVersion>>> _getVersions;
    private readonly ModResolutionService _resolution;
    private readonly ModInstallService _installs;
    private readonly GameProfiles _profiles;
    private readonly DashboardViewModel _dashboard;
    private readonly ModLogos _logos;
    private readonly FavouritesStore _favourites;
    private readonly SettingsStore? _settings;
    private readonly ILogger _log;
    private bool _restoring;

    // The newest choices; whichever background save runs last writes these, so saves can't land out of order.
    private (string View, string Sort, string Filter) _latestChoices;

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
    private IReadOnlyList<CatalogMod> _matches = [];
    private bool _endlessScroll;
    private int _modsPageSize;
    private int _currentPage = 1;
    private bool _resetWindowOnSearch = true;
    private string _resultQuery = "";
    private ModFilter _resultFilter;
    private int _resultTagCount;

    // Entries include the active operation, so duplicate clicks cannot submit it again.
    private readonly Lock _operationLock = new();
    private readonly Queue<ModOperation> _operations = new();
    private readonly List<string> _operationOutcomes = [];
    private ModOperation? _activeOperation;
    private bool _processingOperations;

    /// <param name="getVersions">A mod's versions; the flag skips the cache.</param>
    public ModsViewModel(ModCatalogStore store, Func<string, bool, CancellationToken, Task<IReadOnlyList<CatalogVersion>>> getVersions,
        ModResolutionService resolution, ModInstallService installs, GameProfiles profiles, DashboardViewModel dashboard, ModLogos logos,
        FavouritesStore favourites, ILogger<ModsViewModel> log, SettingsStore? settings = null)
    {
        _store = store;
        _getVersions = getVersions;
        _resolution = resolution;
        _installs = installs;
        _profiles = profiles;
        _dashboard = dashboard;
        _logos = logos;
        DescriptionImageResolver = new ModDescriptionImageResolver(logos);
        _favourites = favourites;
        _settings = settings;
        _log = log;
        var browsing = settings?.Current ?? AppSettings.Default;
        _endlessScroll = browsing.EndlessScroll;
        _modsPageSize = browsing.ModsPageSize;
        if (settings is not null)
        {
            settings.Changed += OnSettingsChanged;
        }
        RestoreChoices();

        dashboard.Installs.CollectionChanged += OnInstallsChanged;
        dashboard.PropertyChanged += OnDashboardChanged;
        dashboard.Catalog.PropertyChanged += OnCatalogChanged;
        SelectedGame = dashboard.SelectedInstall ?? dashboard.Installs.FirstOrDefault();
    }

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

    public bool IsListView { get => View == ModView.List; set { if (value) View = ModView.List; } }

    public bool IsGridView { get => View == ModView.Grid; set { if (value) View = ModView.Grid; } }

    public bool IsCardView { get => View == ModView.Card; set { if (value) View = ModView.Card; } }

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
        + (IsGameRunning ? " until Satisfactory closes." : ".");

    /// <summary>The batch button submits work behind the current operation when the worker is busy.</summary>
    public string InstallQueuedButtonText => IsBusy ? "Queue installs" : "Install queued";

    /// <summary>Completes when the current worker drains the queue or pauses for the game; for tests.</summary>
    internal Task PendingOperations { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UninstallCommand), nameof(InstallQueuedCommand))]
    [NotifyCanExecuteChangedFor(nameof(QuickChangeCommand))]
    [NotifyPropertyChangedFor(nameof(GameRunningText), nameof(OperationQueueText))]
    public partial bool IsGameRunning { get; set; }

    public string? GameRunningText => IsGameRunning
        ? "Satisfactory is running. Close it to install or uninstall mods."
        : null;

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    /// <summary>Outcomes of the current run, retained while subsequent operations execute.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    private bool CanPlan => SelectedGame is { } game && SelectedMod is { } mod
        && !IsGameRunning && !IsActiveOperationFor(game, mod.Reference);

    private bool CanInstall => CanPlan && SelectedMod is { CanInstallHere: true };

    private bool CanUninstall => CanPlan && SelectedMod is { ShowUninstall: true };

    private bool CanQueue => SelectedGame is not null && SelectedMod is { CanBeRequested: true };

    private bool CanInstallQueued => HasQueue && SelectedGame is { } game && !IsGameRunning
        && CurrentQueue()!.Any(reference => FindOperation(game, reference) is null);

    /// <summary>Completes when the most recent change of layout, sort or filter is saved; for tests.</summary>
    internal Task PendingChoicesSave { get; private set; } = Task.CompletedTask;

    /// <summary>Completes when the most recent search has shown its results; for tests.</summary>
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;

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

    partial void OnSearchTextChanged(string value) => ScheduleSearch(SearchDelay);

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
        if (_settings is null)
            return;
        _restoring = true;
        try
        {
            var saved = _settings.Current;
            if (Enum.TryParse<ModView>(saved.ModsView, out var view))
                View = view;
            if (Enum.TryParse<ModSort>(saved.ModsSort, out var sort) && Sorts.FirstOrDefault(o => o.Value == sort) is { } sortOption)
                Sort = sortOption;
            // The queue lasts a session, so a remembered "Queued" would open on an empty list.
            if (Enum.TryParse<ModFilter>(saved.ModsFilter, out var filter) && filter != ModFilter.Queued
                && Filters.FirstOrDefault(o => o.Value == filter) is { } filterOption)
                Filter = filterOption;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't read the Mods page's saved choices");
        }
        finally
        {
            _restoring = false;
        }
    }

    private void SaveChoices()
    {
        if (_settings is not { } settings || _restoring)
            return;
        _latestChoices = (View.ToString(), Sort.Value.ToString(), Filter.Value.ToString());
        PendingChoicesSave = Task.Run(() =>
        {
            try
            {
                settings.Update(s =>
                {
                    var (view, sort, filter) = _latestChoices;
                    return s with { ModsView = view, ModsSort = sort, ModsFilter = filter };
                });
            }
            catch (Exception e)
            {
                // Cosmetic: the page still works; it just opens on the defaults next time.
                _log.LogWarning(e, "Couldn't save the Mods page's choices");
            }
        });
    }

    partial void OnMatchAllTagsChanged(bool value) => ScheduleSearch(TimeSpan.Zero);

    [RelayCommand]
    private void ClearTags()
    {
        foreach (var tag in Tags)
            tag.IsSelected = false;
    }

    /// <summary>Adds the selected mod to the game's install queue, or takes it out.</summary>
    [RelayCommand(CanExecute = nameof(CanQueue))]
    private void ToggleQueue()
    {
        if (SelectedMod is not { } mod || CurrentQueue() is not { } queue)
            return;
        mod.IsQueued = queue.Add(mod.Reference) || !queue.Remove(mod.Reference);
        QueueCount = queue.Count;
        if (Filter.Value == ModFilter.Queued)
            ScheduleSearch(TimeSpan.Zero);
    }

    [RelayCommand]
    private void ClearQueue()
    {
        CurrentQueue()?.Clear();
        QueueCount = 0;
        foreach (var item in Results)
            item.IsQueued = false;
        if (Filter.Value == ModFilter.Queued)
            ScheduleSearch(TimeSpan.Zero);
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
            return null;
        if (!_queues.TryGetValue(game.Install.GamePath, out var queue))
            _queues[game.Install.GamePath] = queue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return queue;
    }

    partial void OnSelectedGameChanged(GameInstallViewModel? value)
    {
        if (!ReferenceEquals(_dashboard.SelectedInstall, value))
        {
            _dashboard.SelectedInstall = value;
        }
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
            InstallCommand.NotifyCanExecuteChanged();
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
            return;
        _pendingDetails = new CancellationTokenSource();
        PendingDetails = LoadDetailsAsync(mod, _pendingDetails.Token);
    }

    /// <summary>Completes when the open mod page has its versions, dependencies and gallery; for tests.</summary>
    internal Task PendingDetails { get; private set; } = Task.CompletedTask;

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
        return mod is not null && SelectedGame is { } game && !IsGameRunning
            && !IsActiveOperationFor(game, mod.Reference);
    }

    /// <summary>
    /// Copies request data at the click. A mod already submitted for this game keeps its existing
    /// place and action; overlapping batch entries are not submitted a second time.
    /// </summary>
    private Task EnqueueOperation(GameInstallViewModel game, ModOperationKind kind, IReadOnlyList<string> references, string? version = null)
    {
        if (game.IsSwitchingProfile)
        {
            Notice = "Wait for profile switching to finish before changing mods.";
            return Task.CompletedTask;
        }
        var waiting = new List<Task>();
        lock (_operationLock)
        {
            var newReferences = new List<string>();
            foreach (var reference in references.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (FindOperation(game, reference) is { } existing)
                {
                    waiting.Add(existing.Completion.Task);
                }
                else
                {
                    newReferences.Add(reference);
                }
            }

            if (newReferences.Count > 0)
            {
                if (_operations.Count == 0)
                {
                    _operationOutcomes.Clear();
                    Notice = null;
                    _dashboard.OperationNotice = null;
                }
                var operation = new ModOperation(game, kind, newReferences.ToArray(), version);
                _operations.Enqueue(operation);
                waiting.Add(operation.Completion.Task);
            }
        }
        UpdateOperationState();
        StartOperations();
        return Task.WhenAll(waiting);
    }

    /// <summary>Starts at most one worker; paused requests stay at the head until the game closes.</summary>
    private void StartOperations()
    {
        lock (_operationLock)
        {
            if (_processingOperations || _operations.Count == 0 || IsGameRunning)
            {
                return;
            }
            _processingOperations = true;
        }
        PendingOperations = ProcessOperationsAsync();
    }

    /// <summary>
    /// Processes captured requests in order, recording failures without dropping later work.
    /// A late game start retains the current request for a fresh plan after the game closes.
    /// </summary>
    private async Task ProcessOperationsAsync()
    {
        ModOperation? finishing = null;
        try
        {
            while (true)
            {
                ModOperation operation;
                lock (_operationLock)
                {
                    if (IsGameRunning || !_operations.TryPeek(out var next))
                    {
                        break;
                    }
                    operation = next;
                    _activeOperation = operation;
                }
                IsBusy = true;
                UpdateOperationState();

                try
                {
                    RecordOperationOutcome(await PlanAndApplyAsync(operation));
                }
                catch (GameRunningException)
                {
                    // The install engine can see a start before the process watcher. Publish it
                    // there too, so even a short game session produces the later close notification.
                    _dashboard.IsGameRunning = true;
                    foreach (var game in Games)
                    {
                        game.IsGameRunning = true;
                    }
                    IsGameRunning = true;
                    break;
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Changing mods in {Game} failed", operation.Game.Install.GamePath);
                    _dashboard.ReportOperationProblem($"{operation.Game.Install.DisplayName}: {e.Message}");
                    RecordOperationOutcome($"Couldn't {operation.Kind.ToString().ToLowerInvariant()} {string.Join(", ", operation.References)}: {e.Message}");
                }

                try
                {
                    await RefreshAfterOperationAsync(operation.Game);
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Refreshing mods in {Game} failed", operation.Game.Install.GamePath);
                    _dashboard.ReportOperationProblem($"Couldn't refresh {operation.Game.Install.DisplayName}: {e.Message}");
                    RecordOperationOutcome($"Couldn't refresh the displayed state: {e.Message}");
                }

                lock (_operationLock)
                {
                    _operations.Dequeue();
                    _activeOperation = null;
                    finishing = operation;
                }
                UpdateOperationState();

                lock (_operationLock)
                {
                    if (_operations.Count == 0 || IsGameRunning)
                    {
                        break;
                    }
                }
                finishing.Completion.TrySetResult();
                finishing = null;
            }
        }
        finally
        {
            lock (_operationLock)
            {
                _activeOperation = null;
            }
            ProgressText = null;
            IsBusy = false;
            UpdateOperationState();
            lock (_operationLock)
            {
                _processingOperations = false;
            }
            finishing?.Completion.TrySetResult();
            StartOperations();
        }
    }

    /// <summary>Plans against the current disk state only when this request reaches the head.</summary>
    private async Task<string> PlanAndApplyAsync(ModOperation operation)
    {
        var game = operation.Game.Install;
        ProgressText = $"Working out what needs to change for {string.Join(", ", operation.References)}…";
        var plan = operation.Kind == ModOperationKind.Uninstall
            ? await _resolution.PlanUninstallAsync(game, operation.References[0])
            : operation.Version is { } version
                ? await _resolution.PlanInstallVersionAsync(game, operation.References[0], version)
                : await _resolution.PlanInstallAsync(game, operation.References);

        if (IsGameRunning)
        {
            throw new GameRunningException();
        }
        if (!plan.Succeeded)
        {
            _dashboard.ReportOperationProblem($"{game.DisplayName}: {string.Join(" ", plan.Problems)}");
            return string.Join("\n", plan.Problems);
        }
        if (plan.ChangesNothing)
        {
            return "Already in the requested state; nothing to change.";
        }

        ProgressText = "Applying changes…";
        var progress = new Progress<ModInstallProgress>(p =>
        {
            if (ReferenceEquals(_activeOperation, operation))
            {
                ProgressText = PlanText.Progress(p);
            }
        });
        var result = await _resolution.ApplyAsync(game, plan, progress);
        var outcome = Describe(result);
        try
        {
            await _profiles.RecordInstalledAsync(game);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Updating the profile for {Game} failed", game.GamePath);
            outcome += $" Couldn't update the profile: {e.Message}";
        }
        return outcome;
    }

    /// <summary>Retains every outcome in this run so the next request cannot hide an earlier failure.</summary>
    private void RecordOperationOutcome(string outcome)
    {
        _operationOutcomes.Add(outcome);
        Notice = string.Join("\n", _operationOutcomes);
    }

    /// <summary>Updates the captured game's batch and profile display, then the currently visible game.</summary>
    private async Task RefreshAfterOperationAsync(GameInstallViewModel game)
    {
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

    private ModOperation? FindOperation(GameInstallViewModel game, string reference)
    {
        lock (_operationLock)
        {
            return _operations.FirstOrDefault(operation => operation.Game.Install.GamePath == game.Install.GamePath
                && operation.References.Contains(reference, StringComparer.OrdinalIgnoreCase));
        }
    }

    private bool IsActiveOperationFor(GameInstallViewModel game, string reference)
    {
        lock (_operationLock)
        {
            return _activeOperation is { } active && active.Game.Install.GamePath == game.Install.GamePath
                && active.References.Contains(reference, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Reapplies operation state after queue changes and to replacement search results.</summary>
    private void ApplyOperationState(ModItemViewModel item)
    {
        lock (_operationLock)
        {
            var operations = _operations.ToArray();
            var operation = SelectedGame is { } game ? FindOperation(game, item.Reference) : null;
            item.IsOperationQueueBusy = operations.Length > 0;
            item.PendingUninstall = operation is null ? null : operation.Kind == ModOperationKind.Uninstall;
            item.OperationPosition = operation is null ? 0 : Array.IndexOf(operations, operation) + 1;
            item.IsOperationRunning = operation is not null && ReferenceEquals(operation, _activeOperation);
        }
    }

    private void UpdateOperationState()
    {
        lock (_operationLock)
        {
            PendingOperationCount = _operations.Count - (_activeOperation is null ? 0 : 1);
            foreach (var game in Games)
            {
                game.HasModOperations = _operations.Any(o => o.Game.Install.GamePath == game.Install.GamePath);
            }
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
            StartOperations();
        }
    }

    private enum ModOperationKind
    {
        Install,
        Uninstall
    }

    /// <summary>Immutable request data and a completion signal shared by duplicate submissions.</summary>
    private sealed record ModOperation(GameInstallViewModel Game, ModOperationKind Kind, IReadOnlyList<string> References, string? Version)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            ScheduleSearch(TimeSpan.Zero);
    }

    private async Task LoadLogoAsync(ModItemViewModel item) => item.Logo ??= await _logos.GetAsync(item.Mod.LogoUrl);

    private async Task LoadTagsAsync()
    {
        if (Tags.Count > 0)
            return;
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
            return;
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
            ApplyInstalled(item);
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    private void ApplyInstalled(ModItemViewModel item)
    {
        item.InstalledVersion = _installed.GetValueOrDefault(item.Reference)?.Version;
        item.IsDependencyOnly = item.IsInstalled && !_requested.Contains(item.Reference);
        ApplyOperationState(item);
    }

    /// <summary>Fills in a mod's page: versions and changelogs, dependencies and gallery. Never throws.</summary>
    private async Task LoadDetailsAsync(ModItemViewModel item, CancellationToken ct)
    {
        var logo = _logos.GetAsync(item.Mod.LogoUrl);
        item.Gallery = ModDescription.ImageUrls(item.Mod.FullDescription).Select(u => new GalleryImageViewModel(u)).ToList();
        foreach (var image in item.Gallery)
            _ = LoadGalleryImageAsync(image);

        try
        {
            var versions = await _getVersions(item.Reference, false, ct);
            // Lists cached before changelogs were fetched have none; fetch once more.
            if (versions.Count > 0 && versions.All(v => v.Changelog is null))
                versions = await _getVersions(item.Reference, true, ct);
            ShowVersions(item, versions);
            item.Dependencies = await DependencyRowsAsync(item, versions, ct);
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

    private async Task LoadGalleryImageAsync(GalleryImageViewModel image) => image.Image = await _logos.GetAsync(image.Url, 960);

    private void ShowVersions(ModItemViewModel item, IReadOnlyList<CatalogVersion> versions)
    {
        var game = SelectedGame?.Install;
        var platform = game is null ? TargetPlatform.Windows : ModInstallService.TargetFor(game);
        bool Supports(CatalogVersion v) => game?.Build is { } build && v.TargetFor(platform) is not null
            && GameVersionRange.TryParse(v.GameVersionRange, out var range) && range.Contains(build.Changelist);

        var newestFirst = versions.OrderByDescending(v => v.CreatedAt).ToList();
        item.Versions = newestFirst
            .Select(v => new VersionRowViewModel(v, Supports(v), v.Version == item.InstalledVersion, v.TargetFor(platform)?.Size))
            .ToList();

        var compatible = newestFirst.Where(Supports).ToList();
        var latest = compatible.OrderBy(v => v.Stability).FirstOrDefault();
        item.VersionChoices = [new(null, "Latest compatible"), .. compatible.Select(v => new Option<string?>(v.Version,
            v.Stability == VersionStability.Release ? v.Version : $"{v.Version} ({v.Stability})"))];
        item.SelectedVersionChoice = item.VersionChoices[0];
        item.SizeText = latest?.TargetFor(platform)?.Size is { } size ? SettingsViewModel.FormatSize(size) : null;

        if (game?.Build is not { } gameBuild)
        {
            item.LatestText = null;
            return;
        }
        item.LatestText = latest is null
            ? $"No version supports game build {gameBuild.Changelist} yet."
            : $"Latest for this game: {latest.Version}{(latest.Stability == VersionStability.Release ? "" : $" ({latest.Stability})")}";
    }

    private async Task<IReadOnlyList<DependencyRowViewModel>> DependencyRowsAsync(ModItemViewModel item, IReadOnlyList<CatalogVersion> versions,
        CancellationToken ct)
    {
        // What the version that would be installed needs: the newest compatible, else the newest.
        var shown = item.Versions.FirstOrDefault(v => v.SupportsGame)?.Version ?? item.Versions.FirstOrDefault()?.Version;
        var version = versions.FirstOrDefault(v => v.Version == shown);
        if (version is null)
            return [];

        var installed = _installed;
        return await Task.Run(() => version.Dependencies
            .Select(d => new DependencyRowViewModel(d.ModReference, _store.Get(d.ModReference)?.Name ?? d.ModReference, d.Condition,
                d.Optional, installed.ContainsKey(d.ModReference)))
            .ToList(), ct);
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
        if (e.PropertyName == nameof(DashboardViewModel.SelectedInstall)
            && !ReferenceEquals(SelectedGame, _dashboard.SelectedInstall))
        {
            SelectedGame = _dashboard.SelectedInstall;
        }
        if (e.PropertyName == nameof(DashboardViewModel.IsGameRunning))
            IsGameRunning = _dashboard.IsGameRunning;
    }

    // The first launch downloads the mod list after the page may already be open.
    private void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogStatusViewModel.IsChecking) && !_dashboard.Catalog.IsChecking)
        {
            if (Tags.Count == 0)
                _ = LoadTagsAsync();
            ScheduleSearch(TimeSpan.Zero);
        }
    }
}

/// <summary>One tag in the Mods page's tag list.</summary>
public partial class TagOptionViewModel(string name, int count) : ViewModelBase
{
    public string Name { get; } = name;

    public int Count { get; } = count;

    public string Label => $"{Name} ({Count:N0})";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
