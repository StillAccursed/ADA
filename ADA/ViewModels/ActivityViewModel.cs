using System.Collections.ObjectModel;
using System.Globalization;
using ADA.Core.Activity;
using ADA.Infrastructure.Activity;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>
/// The Activity page: what ADA did, newest first and grouped by day, filtered by area, install, failures and search.
/// Reads the history the first time it's shown, then keeps it current from <see cref="ActivityLog.Recorded"/> and
/// <see cref="ActivityLog.Cleared"/>, posted to the page's thread. Shows <see cref="PAGE_SIZE"/> matching entries
/// at a time, because the grouped list can't be virtualised. Its Logs tab is <see cref="Logs"/>.
/// </summary>
public partial class ActivityViewModel : ViewModelBase
{
    /// <summary>How many entries are shown at first, and how many each "Show more" adds.</summary>
    internal const int PAGE_SIZE = 200;

    private static readonly ActivityFilterOption<string?> ALL_INSTALLS = new("All installs", null);

    private readonly ActivityLog _activity;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    // Every entry, newest first, and their ids: an entry recorded while the first read runs may be in both.
    private readonly List<ActivityEntryViewModel> _all = [];
    private readonly HashSet<Guid> _ids = [];

    // Entries recorded while the first read was running; merged in when it finishes.
    private readonly List<ActivityEntry> _early = [];

    private SynchronizationContext? _viewContext;
    private bool _listening;
    private bool _loaded;
    private int _limit = PAGE_SIZE;

    // The install filter, kept apart from the ComboBox's selection, which drops to null while its options are replaced.
    private string? _installKey;

    /// <summary>The shown entries, one group per local day, newest first.</summary>
    public ObservableCollection<ActivityDayViewModel> Days { get; } = [];

    public IReadOnlyList<ActivityFilterOption<ActivityArea?>> Areas { get; } =
    [
        new("All areas", null),
        .. Enum.GetValues<ActivityArea>().Select(a => new ActivityFilterOption<ActivityArea?>(ActivityEntryViewModel.AreaName(a), a)),
    ];

    [ObservableProperty]
    public partial ActivityFilterOption<ActivityArea?> SelectedArea { get; set; }

    /// <summary>"All installs", then every install in the history by name.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ActivityFilterOption<string?>> Installs { get; private set; } = [ALL_INSTALLS];

    [ObservableProperty]
    public partial ActivityFilterOption<string?>? SelectedInstall { get; set; } = ALL_INSTALLS;

    [ObservableProperty]
    public partial bool FailuresOnly { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEntries))]
    [NotifyCanExecuteChangedFor(nameof(ClearHistoryCommand))]
    public partial int Total { get; private set; }

    public bool HasEntries => Total > 0;

    /// <summary>E.g. "1,234 entries", or "12 of 1,234 match" while filtering.</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    /// <summary>Why nothing is listed; null while something is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsList))]
    public partial string? EmptyText { get; private set; } = "Reading the activity history…";

    public bool ShowsList => EmptyText is null;

    /// <summary>The "Show more" button's text; null when every match is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    public partial string? ShowMoreText { get; private set; }

    public bool HasMore => ShowMoreText is not null;

    /// <summary>Why the history couldn't be read or cleared; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>A question for the page's view to show as a dialog.</summary>
    [ObservableProperty]
    public partial ConfirmViewModel? Confirmation { get; private set; }

    public string FolderPath => _activity.Folder;

    /// <summary>The first read; for tests.</summary>
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The Logs tab.</summary>
    public LogsViewModel Logs { get; }

    /// <param name="time">The clock and time zone days are grouped by; the system's by default.</param>
    public ActivityViewModel(ActivityLog activity, LogsViewModel logs, ILogger<ActivityViewModel> log, TimeProvider? time = null)
    {
        (_activity, Logs, _log, _time) = (activity, logs, log, time ?? TimeProvider.System);
        SelectedArea = Areas[0];
    }

    /// <summary>
    /// Called by the view each time the page is shown. The first time, it captures the page's thread, starts
    /// listening for new entries, and reads the history; later calls do nothing unless that read failed. Never throws.
    /// </summary>
    internal Task ShowAsync()
    {
        if (!_listening)
        {
            _viewContext = SynchronizationContext.Current;
            _activity.Recorded += OnRecorded;
            _activity.Cleared += OnCleared;
            _listening = true;
        }
        if (!_loaded && PendingLoad.IsCompleted)
        {
            PendingLoad = LoadAsync();
        }
        return PendingLoad;
    }

    /// <summary>"Today", "Yesterday", "Thursday 29 October", or with the year for an earlier year.</summary>
    internal static string DayHeader(DateTime day, DateTime today) => (today.Date - day.Date).Days switch
    {
        0 => "Today",
        1 => "Yesterday",
        _ when day.Year == today.Year => day.ToString("dddd d MMMM", CultureInfo.CurrentCulture),
        _ => day.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture),
    };

    [RelayCommand]
    private void ShowMore()
    {
        _limit += PAGE_SIZE;
        Apply();
    }

    [RelayCommand(CanExecute = nameof(HasEntries))]
    private async Task ClearHistoryAsync()
    {
        var question = new ConfirmViewModel("Clear the activity history?",
            $"{(Total == 1 ? "The only entry" : $"All {Total:N0} entries")} will be deleted. Mods, profiles and backups aren't affected. This can't be undone.",
            "Clear")
        {
            IsDanger = true,
        };
        Confirmation = question;
        var confirmed = await question.Answer;
        Confirmation = null;
        if (!confirmed)
        {
            return;
        }

        try
        {
            // The list empties from the Cleared event, as it would for a clear made anywhere else.
            await _activity.ClearAsync();
            Notice = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Clearing the activity history failed");
            Notice = $"Couldn't clear the history: {e.Message}";
        }
    }

    partial void OnSelectedAreaChanged(ActivityFilterOption<ActivityArea?> value) => FilterChanged();

    partial void OnSelectedInstallChanged(ActivityFilterOption<string?>? value)
    {
        // Null while the options are being replaced; the filter stays as it was.
        if (value is not null)
        {
            _installKey = value.Value;
            FilterChanged();
        }
    }

    partial void OnFailuresOnlyChanged(bool value) => FilterChanged();

    partial void OnSearchTextChanged(string value) => FilterChanged();

    private async Task LoadAsync()
    {
        EmptyText = "Reading the activity history…";
        try
        {
            foreach (var entry in await _activity.ReadAsync())
            {
                Add(entry);
            }
            foreach (var entry in _early)
            {
                Add(entry);
            }
            _early.Clear();
            _loaded = true;
            Notice = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Reading the activity history failed");
            Notice = $"Couldn't read the activity history: {e.Message}";
        }
        Apply();
    }

    private void OnRecorded(object? sender, ActivityEntry entry) => OnView(() =>
    {
        if (!_loaded)
        {
            _early.Add(entry);
            return;
        }
        Add(entry);
        Apply();
    });

    private void OnCleared(object? sender, EventArgs e) => OnView(() =>
    {
        _all.Clear();
        _ids.Clear();
        _early.Clear();
        Apply();
    });

    /// <summary>Inserts an entry in newest-first order, ahead of any with the same time; skips one already listed.</summary>
    private void Add(ActivityEntry entry)
    {
        if (!_ids.Add(entry.Id))
        {
            return;
        }
        var index = _all.FindIndex(e => e.Entry.AtUtc <= entry.AtUtc);
        _all.Insert(index < 0 ? _all.Count : index, new ActivityEntryViewModel(entry, _time.LocalTimeZone));
    }

    private void FilterChanged()
    {
        _limit = PAGE_SIZE;
        Apply();
    }

    /// <summary>Rebuilds the install options, the shown days and the texts from <see cref="_all"/> and the filters.</summary>
    private void Apply()
    {
        Total = _all.Count;
        UpdateInstalls();

        var query = SearchText.Trim();
        var matching = _all.Where(e => Matches(e, query)).ToList();
        var today = _time.GetLocalNow().Date;
        Days.Clear();
        foreach (var day in matching.Take(_limit).GroupBy(e => e.Local.Date))
        {
            Days.Add(new ActivityDayViewModel(DayHeader(day.Key, today), day.ToList()));
        }

        var remaining = matching.Count - Math.Min(_limit, matching.Count);
        ShowMoreText = remaining > 0 ? $"Show {Math.Min(remaining, PAGE_SIZE):N0} more" : null;
        CountText = matching.Count == Total ? Count(Total) : $"{matching.Count:N0} of {Count(Total)} match";
        EmptyText = (Total, matching.Count) switch
        {
            (_, > 0) => null,
            (0, _) when !_loaded => Notice is null ? "Reading the activity history…" : "The history couldn't be read.",
            (0, _) => "Nothing recorded yet. Mod changes, profile switches, backups and more appear here.",
            _ => "No activity matches these filters.",
        };
    }

    private bool Matches(ActivityEntryViewModel item, string query) =>
        (SelectedArea.Value is not { } area || item.Entry.Area == area)
        && (_installKey is null || item.Entry.InstallKey == _installKey)
        && (!FailuresOnly || item.IsFailed)
        && item.Matches(query);

    /// <summary>
    /// Replaces the install options when the history's installs change, keeping the chosen one. Installs recorded under
    /// the same name (two Epic copies are both "Satisfactory (Epic)") are told apart by their folder.
    /// </summary>
    private void UpdateInstalls()
    {
        var named = _all.Where(e => e.Entry.InstallKey is not null)
            .DistinctBy(e => e.Entry.InstallKey)
            .Select(e => (Name: e.Entry.Install ?? e.Entry.InstallKey!, Key: e.Entry.InstallKey!))
            .ToList();
        var shared = named.GroupBy(i => i.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        List<ActivityFilterOption<string?>> options =
        [
            ALL_INSTALLS,
            .. named
                .Select(i => new ActivityFilterOption<string?>(shared.Contains(i.Name) ? $"{i.Name} · {i.Key}" : i.Name, i.Key))
                .OrderBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase),
        ];
        if (options.SequenceEqual(Installs))
        {
            return;
        }

        Installs = options;
        // An install whose entries were all cleared can't be chosen any more.
        SelectedInstall = options.FirstOrDefault(o => o.Value == _installKey) ?? ALL_INSTALLS;
    }

    /// <summary>Runs on the page's thread: ActivityLog raises its events on whichever thread recorded.</summary>
    private void OnView(Action action)
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

    private static string Count(int entries) => entries == 1 ? "1 entry" : $"{entries:N0} entries";
}
