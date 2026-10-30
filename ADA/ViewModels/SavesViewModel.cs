using System.Collections.ObjectModel;
using System.ComponentModel;
using ADA.Core.Games;
using ADA.Core.Saves;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Saves;
using ADA.Infrastructure.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>How the Saves page orders sessions.</summary>
public enum SaveSort
{
    LastSaved,
    Name,
    PlayTime,
}

/// <summary>
/// The Saves page, three levels deep: the sidebar install's sessions in the same layouts as Mods, one
/// session's saves and blueprints, and one save's page. Re-reads each time it's shown, and while shown
/// whenever the game writes a save or mods are installed or removed; those events arrive on worker threads
/// and are posted to the page's thread. Overlapping reads cancel the older one, so only the newest result
/// is applied.
/// </summary>
public partial class SavesViewModel : ViewModelBase
{
    private const int MAX_PROFILE_NAME_ATTEMPTS = 99;

    private const string CLOSE_GAME_FOR_BLUEPRINTS =
        "Close Satisfactory first. It only reads blueprints when a session loads, and an open blueprint can't be replaced.";

    private const string CANT_SWITCH_NOW = "Close Satisfactory and wait for current operations to finish before switching mods.";

    private static readonly IReadOnlyList<Option<SaveSort>> SORTS =
    [
        new(SaveSort.LastSaved, "Last saved"),
        new(SaveSort.Name, "Name"),
        new(SaveSort.PlayTime, "Playtime"),
    ];

    private readonly DashboardViewModel _dashboard;
    private readonly SaveLibrary _library;
    private readonly BlueprintTransfer _transfer;
    private readonly ModInstallService _installs;
    private readonly GameProfiles _profiles;
    private readonly SettingsStore _settings;
    private readonly ILogger _log;
    private SynchronizationContext? _viewContext;
    private bool _isShown;
    private SaveFolderWatcher? _watcher;
    private string? _watchedPath;
    private CancellationTokenSource? _pendingRead;
    private string? _listedFolder;
    private IReadOnlyDictionary<string, InstalledMod>? _installed;
    private IReadOnlyList<SessionItemViewModel> _all = [];

    /// <summary>
    /// Raised with a save's missing mod references when the user asks to install them; the main window adds
    /// them to the Mods page's queue and shows it.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? InstallRequested;

    public IReadOnlyList<Option<SaveSort>> Sorts => SORTS;

    /// <summary>The sessions matching the search, in the chosen order.</summary>
    public ObservableCollection<SessionItemViewModel> Shown { get; } = [];

    /// <summary>Matches session names and the names of their saves.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial Option<SaveSort> Sort { get; set; } = SORTS[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView), nameof(IsGridView), nameof(IsCardView))]
    public partial ModView View { get; set; } = ModView.List;

    public bool IsListView { get => View == ModView.List; set { if (value) { View = ModView.List; } } }

    public bool IsGridView { get => View == ModView.Grid; set { if (value) { View = ModView.Grid; } } }

    public bool IsCardView { get => View == ModView.Card; set { if (value) { View = ModView.Card; } } }

    /// <summary>The open session's page; null while the sessions list shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSessionList), nameof(ShowsSession), nameof(ShowsSave))]
    public partial SessionDetailsViewModel? Session { get; private set; }

    /// <summary>The open save's page, inside <see cref="Session"/>; null while the session's page shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSession), nameof(ShowsSave))]
    [NotifyCanExecuteChangedFor(nameof(SwitchToSaveModsCommand), nameof(InstallMissingModsCommand))]
    public partial SaveDetailsViewModel? Save { get; private set; }

    public bool ShowsSessionList => Session is null;

    public bool ShowsSession => Session is not null && Save is null;

    public bool ShowsSave => Save is not null;

    /// <summary>The sessions list's selection: choosing a session opens its page, then the list is left unselected.</summary>
    [ObservableProperty]
    public partial SessionItemViewModel? SessionSelection { get; set; }

    /// <summary>The session page's selection: choosing a save opens its page, then the list is left unselected.</summary>
    [ObservableProperty]
    public partial SaveItemViewModel? SaveSelection { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Unreadable saves, a failed read, or a page that closed because its session or save went away.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Why a save's mods couldn't be switched to just now; shown on the save page.</summary>
    [ObservableProperty]
    public partial string? ActionNotice { get; private set; }

    /// <summary>A question for the page's view to show as a dialog: what to do with blueprint names that already exist.</summary>
    [ObservableProperty]
    public partial ConfirmViewModel? Confirmation { get; private set; }

    /// <summary>What the last copy, import or export did, or why it didn't; shown on the Blueprints tab.</summary>
    [ObservableProperty]
    public partial string? BlueprintNotice { get; private set; }

    /// <summary>Why there's no list to show; null when there is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEmptyState), nameof(ShowsList))]
    public partial string? EmptyText { get; private set; }

    public bool ShowsEmptyState => EmptyText is not null;

    public bool ShowsList => EmptyText is null;

    /// <summary>E.g. "2 sessions · 5 saves", or "1 of 2 sessions" while searching.</summary>
    [ObservableProperty]
    public partial string ResultsText { get; private set; } = "";

    /// <summary>The most recent read; for tests.</summary>
    internal Task PendingRefresh { get; private set; } = Task.CompletedTask;

    public SavesViewModel(DashboardViewModel dashboard, SaveLibrary library, BlueprintTransfer transfer, ModInstallService installs,
        GameProfiles profiles, SettingsStore settings, ILogger<SavesViewModel> log)
    {
        (_dashboard, _library, _transfer, _installs, _profiles, _settings, _log) =
            (dashboard, library, transfer, installs, profiles, settings, log);
        View = Enum.TryParse<ModView>(settings.Current.SavesView, out var saved) ? saved : ModView.List;
        dashboard.PropertyChanged += OnDashboardChanged;
        installs.Changed += OnInstallsChanged;
    }

    /// <summary>
    /// Called by the view each time the page is shown: captures the page's thread for watcher events, then
    /// re-reads and watches the sidebar install's saves. Never throws.
    /// </summary>
    internal Task ShowAsync()
    {
        _viewContext ??= SynchronizationContext.Current;
        _isShown = true;
        return RefreshAsync();
    }

    /// <summary>Called by the view when the page is left. Stops watching, so nothing re-reads while it's hidden.</summary>
    internal void Hide()
    {
        _isShown = false;
        StopWatching();
    }

    /// <summary>Re-reads the sidebar install's saves, cancelling any read still running. Never throws.</summary>
    internal Task RefreshAsync()
    {
        _pendingRead?.Cancel();
        _pendingRead = new CancellationTokenSource();
        PendingRefresh = ReadAsync(_dashboard.SelectedInstall?.Install, _pendingRead.Token);
        return PendingRefresh;
    }

    /// <returns>Why <paramref name="game"/>'s saves can't be listed, or null when they can.</returns>
    internal static string? WhyNoSaves(GameInstall? game) => game switch
    {
        null => "No Satisfactory install found.",
        { IsRemote: true } => "Saves on dedicated servers can't be shown yet. Choose a local install in the sidebar.",
        { UserDataPath: null } => "ADA can't tell where this install keeps its saves.",
        _ => null,
    };

    /// <summary>Imports blueprints from files the user picked into the open session. Called by the view after its file picker.</summary>
    internal async Task ImportBlueprintsAsync(IReadOnlyList<string> paths)
    {
        if (Session is not { } session || WritableUserData() is not { } userData)
        {
            return;
        }

        try
        {
            var import = await _transfer.ReadImportAsync(paths);
            if (import.Blueprints.Count == 0)
            {
                BlueprintNotice = string.Join(" ", ["No blueprints were found.", .. import.Problems]);
                return;
            }
            await WriteBlueprintsAsync(userData, session.Name, import.Blueprints, import.Problems, "Import", "Imported", $"into {session.Name}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.LogError(e, "Importing blueprints into {Session} failed", session.Name);
            BlueprintNotice = $"Couldn't import blueprints: {e.Message}";
        }
    }

    /// <summary>
    /// Writes the ticked blueprints, or all of them when none is ticked, to <paramref name="zip"/>. Called by the
    /// view with the file it opened from its save picker. Reads only, so it works while the game is running.
    /// </summary>
    internal async Task ExportBlueprintsAsync(Stream zip)
    {
        if (Session is not { } session)
        {
            return;
        }

        try
        {
            var files = await _transfer.ReadAsync(session.ExportScope);
            await _transfer.ExportAsync(files, zip);
            BlueprintNotice = $"Exported {Count(files.Count, "blueprint")}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.LogError(e, "Exporting blueprints from {Session} failed", session.Name);
            BlueprintNotice = $"Couldn't export blueprints: {e.Message}";
        }
    }

    partial void OnSessionSelectionChanged(SessionItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }
        BlueprintNotice = null;
        Session = new SessionDetailsViewModel(value, OtherSessions(value.Name));
        SessionSelection = null;
    }

    partial void OnSaveSelectionChanged(SaveItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }
        ActionNotice = null;
        Save = new SaveDetailsViewModel(value.Save, _installed);
        SaveSelection = null;
    }

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnSortChanged(Option<SaveSort> value) => Filter();

    partial void OnViewChanged(ModView value) => _ = Task.Run(() =>
    {
        try
        {
            if (_settings.Current.SavesView != value.ToString())
            {
                _settings.Update(s => s with { SavesView = value.ToString() });
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't save the Saves page's layout");
        }
    });

    /// <summary>Goes up one level: from a save to its session, or from a session to the list.</summary>
    [RelayCommand]
    private void Back()
    {
        if (Save is not null)
        {
            Save = null;
        }
        else
        {
            Session = null;
        }
    }

    /// <summary>The breadcrumb's first part: back to the sessions list from any level.</summary>
    [RelayCommand]
    private void ShowSessions()
    {
        Save = null;
        Session = null;
    }

    /// <summary>The breadcrumb's middle part: from a save back to its session.</summary>
    [RelayCommand]
    private void ShowSession() => Save = null;

    /// <summary>
    /// Opens the Switch profile dialog for a new profile holding the save's mods at their recorded versions.
    /// The profile is named after the session, made unique, and saved only if the switch succeeds.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanActOnSave))]
    private async Task SwitchToSaveModsAsync()
    {
        if (Save is not { } save || _dashboard.SelectedInstall is not { } game)
        {
            return;
        }

        ActionNotice = null;
        if (await Task.Run(() => FreeProfileName(save.SessionName)) is not { } name)
        {
            ActionNotice = $"Couldn't find a free profile name for \"{save.SessionName}\".";
            return;
        }
        if (!await _dashboard.OpenSwitchToAsync(game, save.ToProfile(name)))
        {
            ActionNotice = CANT_SWITCH_NOW;
        }
    }

    /// <summary>Hands the save's missing mods to the Mods page's queue; nothing is installed until the user confirms there.</summary>
    [RelayCommand(CanExecute = nameof(CanActOnSave))]
    private void InstallMissingMods()
    {
        if (Save is { HasMissingMods: true } save)
        {
            InstallRequested?.Invoke(this, save.MissingReferences);
        }
    }

    /// <summary>Copies the ticked blueprints into <paramref name="targetSession"/>, asking first about names it already has.</summary>
    [RelayCommand]
    private async Task CopyBlueprintsAsync(string? targetSession)
    {
        if (Session is not { } session || targetSession is null || WritableUserData() is not { } userData)
        {
            return;
        }
        if (session.SelectedBlueprints is not { Count: > 0 } chosen)
        {
            BlueprintNotice = "Tick the blueprints to copy first.";
            return;
        }

        try
        {
            var files = await _transfer.ReadAsync(chosen);
            await WriteBlueprintsAsync(userData, targetSession, files, [], "Copy", "Copied", $"to {targetSession}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _log.LogError(e, "Copying blueprints from {From} to {To} failed", session.Name, targetSession);
            BlueprintNotice = $"Couldn't copy blueprints: {e.Message}";
        }
    }

    /// <returns>The install's user-data folder when blueprints can be written now; otherwise null, with the reason in <see cref="BlueprintNotice"/>.</returns>
    private string? WritableUserData()
    {
        if (_dashboard.SelectedInstall is not { Install.UserDataPath: { } userData } game || game.Install.IsRemote)
        {
            return null;
        }
        if (game.IsGameRunning)
        {
            BlueprintNotice = CLOSE_GAME_FOR_BLUEPRINTS;
            return null;
        }
        return userData;
    }

    /// <summary>
    /// Asks once about names <paramref name="session"/> already has (Cancel writes nothing), writes, reports, and re-reads.
    /// </summary>
    /// <param name="action">The confirm button's text, e.g. "Copy".</param>
    /// <param name="done">The report's verb, e.g. "Copied".</param>
    /// <param name="where">The report's destination, e.g. "to Verdana".</param>
    private async Task WriteBlueprintsAsync(string userData, string session, IReadOnlyList<BlueprintFiles> files,
        IReadOnlyList<string> problems, string action, string done, string where)
    {
        var clash = BlueprintClash.KeepBoth;
        var clashes = _transfer.Clashes(userData, session, files.Select(f => f.Name));
        if (clashes.Count > 0)
        {
            var question = new ConfirmViewModel("Some blueprints already exist",
                $"{session} already has {string.Join(", ", clashes)}.", action)
            {
                OptionText = "Replace them. Otherwise both are kept, and the incoming ones get a number, e.g. \"Name (2)\".",
            };
            Confirmation = question;
            var go = await question.Answer;
            Confirmation = null;
            if (!go)
            {
                return;
            }
            clash = question.OptionChecked ? BlueprintClash.Replace : BlueprintClash.KeepBoth;
        }

        var result = await _transfer.WriteAsync(userData, session, files, clash);
        var renamed = result.Renamed.Count == 0 ? "" : $", {result.Renamed.Count} renamed";
        BlueprintNotice = string.Join(" ",
            [$"{done} {Count(result.Written.Count, "blueprint")} {where}{renamed}. Reload the session in the game to see them.", .. problems]);
        await RefreshAsync();
    }

    private IReadOnlyList<string> OtherSessions(string session) =>
        _all.Select(s => s.Name)
            .Where(name => !name.Equals(session, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private bool CanActOnSave() => Save is not null && _dashboard.SelectedInstall is { Install.IsRemote: false };

    /// <returns>
    /// <paramref name="session"/>, or "session (2)", "session (3)"… the first no profile uses; null when none is
    /// free or the session name can't be a profile name.
    /// </returns>
    private string? FreeProfileName(string session)
    {
        for (var attempt = 1; attempt <= MAX_PROFILE_NAME_ATTEMPTS; attempt++)
        {
            var candidate = attempt == 1 ? session : $"{session} ({attempt})";
            if (_profiles.ProblemWithNewName(candidate) is null)
            {
                return candidate;
            }
        }
        return null;
    }

    private async Task ReadAsync(GameInstall? game, CancellationToken ct)
    {
        if (WhyNoSaves(game) is { } reason)
        {
            StopWatching();
            Apply(null, null, reason);
            return;
        }

        var userData = game!.UserDataPath!;
        Watch(userData);
        IsLoading = true;
        try
        {
            var contents = await _library.ReadAsync(userData, ct);
            var installed = await ReadInstalledAsync(game);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            Apply(contents, installed, contents.Saves.Count == 0 && contents.Blueprints.Count == 0
                ? "No saves yet. They'll appear here once you save in the game."
                : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A newer read replaced this one.
        }
        catch (Exception e)
        {
            _log.LogError(e, "Reading saves in {Folder} failed", userData);
            Notice = $"Couldn't read saves: {e.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <returns>The install's mods by reference, ignoring case; null when they can't be read.</returns>
    private async Task<IReadOnlyDictionary<string, InstalledMod>?> ReadInstalledAsync(GameInstall game)
    {
        try
        {
            var installed = await Task.Run(() => _installs.InstallerFor(game).Installed);
            return installed.Values
                .DistinctBy(m => m.Reference, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(m => m.Reference, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Reading installed mods in {Game} failed", game.GamePath);
            return null;
        }
    }

    /// <summary>Replaces the listed sessions, keeping the open session and save open where they still exist.</summary>
    private void Apply(SaveFolderContents? contents, IReadOnlyDictionary<string, InstalledMod>? installed, string? emptyText)
    {
        if (contents?.UserDataPath != _listedFolder)
        {
            // Another install's saves: pages left open from the previous one no longer apply.
            Save = null;
            Session = null;
        }
        _listedFolder = contents?.UserDataPath;
        _installed = installed;
        _all = contents is null ? [] : SessionItemViewModel.From(contents);
        Notice = UnreadableNotice(contents?.UnreadableSaves.Count ?? 0);
        EmptyText = emptyText;
        Filter();
        Reopen();
    }

    private void Reopen()
    {
        if (Session is not { } openSession)
        {
            return;
        }

        if (_all.FirstOrDefault(s => s.Name.Equals(openSession.Name, StringComparison.OrdinalIgnoreCase)) is not { } session)
        {
            Save = null;
            Session = null;
            Notice = $"Session \"{openSession.Name}\" no longer has any saves or blueprints.";
            return;
        }

        var openSave = Save;
        var reopened = new SessionDetailsViewModel(session, OtherSessions(session.Name));
        reopened.KeepSelectionFrom(openSession);
        Session = reopened;
        if (openSave is null)
        {
            return;
        }

        if (session.Saves.FirstOrDefault(s => s.FilePath == openSave.Save.FilePath) is not { } save)
        {
            Save = null;
            Notice = $"\"{openSave.Name}\" is no longer in the save folder.";
            return;
        }

        var details = new SaveDetailsViewModel(save, _installed) { ModSearch = openSave.ModSearch };
        Save = details;
    }

    private void Filter()
    {
        var search = SearchText.Trim();
        var matching = _all.Where(s => search.Length == 0 || s.Matches(search));
        var ordered = Sort.Value switch
        {
            SaveSort.Name => matching.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase),
            SaveSort.PlayTime => matching.OrderByDescending(s => s.PlayTime),
            _ => matching.OrderByDescending(s => s.LastSavedUtc ?? DateTime.MinValue),
        };

        Shown.Clear();
        foreach (var item in ordered)
        {
            Shown.Add(item);
        }

        var saves = _all.Sum(s => s.Saves.Count);
        ResultsText = Shown.Count == _all.Count
            ? $"{Count(_all.Count, "session")} · {Count(saves, "save")}"
            : $"{Shown.Count} of {Count(_all.Count, "session")}";
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string? UnreadableNotice(int count) => count switch
    {
        0 => null,
        1 => "1 save couldn't be read. It may be damaged, or still being written by the game.",
        _ => $"{count} saves couldn't be read. They may be damaged, or still being written by the game.",
    };

    /// <summary>Watches the folder while the page is shown; re-watches once a folder that was missing appears.</summary>
    private void Watch(string userDataPath)
    {
        if (!_isShown || (userDataPath == _watchedPath && _watcher is { IsWatching: true }))
        {
            return;
        }
        StopWatching();
        _watcher = _library.Watch(userDataPath);
        _watcher.Changed += OnSavesChanged;
        _watchedPath = userDataPath;
    }

    private void StopWatching()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnSavesChanged;
            _watcher.Dispose();
        }
        _watcher = null;
        _watchedPath = null;
    }

    private void OnSavesChanged(object? sender, EventArgs e) => OnViewThread(() => _ = RefreshAsync());

    // After a switch or an install, the Mods tab's statuses are out of date.
    private void OnInstallsChanged(object? sender, EventArgs e)
    {
        if (_isShown)
        {
            OnViewThread(() => _ = RefreshAsync());
        }
    }

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.SelectedInstall))
        {
            InstallMissingModsCommand.NotifyCanExecuteChanged();
            SwitchToSaveModsCommand.NotifyCanExecuteChanged();
            if (_isShown)
            {
                OnViewThread(() => _ = RefreshAsync());
            }
        }
    }

    private void OnViewThread(Action action)
    {
        if (_viewContext is { } context && !ReferenceEquals(context, SynchronizationContext.Current))
        {
            context.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }
}
