using System.Globalization;
using ADA.Infrastructure.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>
/// The Activity page's Logs tab: one day of ADA's log at a time, oldest first, filtered by level and search. Re-reads
/// the days and the chosen day each time the tab is opened and on Refresh, keeping the chosen day where it still exists.
/// </summary>
public partial class LogsViewModel : ViewModelBase
{
    private readonly LogFiles _files;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private IReadOnlyList<LogDay> _days = [];
    private IReadOnlyList<LogLineViewModel> _lines = [];
    private CancellationTokenSource? _reading;

    // The chosen day, kept apart from the ComboBox, which drops to null while its options are replaced.
    private DateOnly? _date;
    private bool _replacingDays;

    public IReadOnlyList<ActivityFilterOption<LogLevel>> Levels { get; } =
    [
        new("All levels", LogLevel.Trace),
        new("Warnings and errors", LogLevel.Warning),
        new("Errors only", LogLevel.Error),
    ];

    [ObservableProperty]
    public partial ActivityFilterOption<LogLevel> SelectedLevel { get; set; }

    /// <summary>Each day with a log, newest first, e.g. "Today · 284 KB".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ActivityFilterOption<DateOnly?>> Days { get; private set; } = [];

    [ObservableProperty]
    public partial ActivityFilterOption<DateOnly?>? SelectedDay { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>The chosen day's messages that match the filters, oldest first; replaced whole, so the list redraws once.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    public partial IReadOnlyList<LogLineViewModel> Shown { get; private set; } = [];

    /// <summary>The row whose whole message shows in the details pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial LogLineViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    /// <summary>E.g. "1,352 messages", or "3 of 1,352 messages match".</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = "";

    /// <summary>Why nothing is listed; null while something is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsList))]
    public partial string? EmptyText { get; private set; } = "Reading the log…";

    public bool ShowsList => EmptyText is null;

    /// <summary>Said when only the end of a very large day was read; null otherwise.</summary>
    [ObservableProperty]
    public partial string? TruncatedText { get; private set; }

    /// <summary>Why the log couldn't be read; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>What the last Copy put on the clipboard.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>Raised by Copy with the text for the view to put on the clipboard.</summary>
    public event EventHandler<string>? CopyRequested;

    public string FolderPath => _files.Folder;

    /// <summary>The most recent read; for tests.</summary>
    internal Task PendingRead { get; private set; } = Task.CompletedTask;

    /// <param name="time">The clock "Today" is judged by, and the zone times are shown in; the system's by default.</param>
    public LogsViewModel(LogFiles files, ILogger<LogsViewModel> log, TimeProvider? time = null)
    {
        (_files, _log, _time) = (files, log, time ?? TimeProvider.System);
        SelectedLevel = Levels[0];
    }

    /// <summary>Called by the view each time the tab is opened: re-reads the days, then the chosen (or newest) day. Never throws.</summary>
    internal Task ShowAsync()
    {
        PendingRead = ReadDaysAsync();
        return PendingRead;
    }

    [RelayCommand]
    private Task RefreshAsync() => ShowAsync();

    /// <summary>Asks the view to copy the shown messages, whole and as written, one after another.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        CopyRequested?.Invoke(this, string.Join('\n', Shown.Select(l => l.Event.Raw)));
        Status = Shown.Count == 1 ? "Copied 1 message." : $"Copied {Shown.Count:N0} messages.";
    }

    private bool CanCopy() => Shown.Count > 0;

    partial void OnSelectedDayChanged(ActivityFilterOption<DateOnly?>? value)
    {
        // Null while the options are replaced, and replacing them reads the day itself.
        if (_replacingDays || value is null)
        {
            return;
        }
        _date = value.Value;
        PendingRead = ReadDayAsync();
    }

    partial void OnSelectedLevelChanged(ActivityFilterOption<LogLevel> value) => Apply();

    partial void OnSearchTextChanged(string value) => Apply();

    private async Task ReadDaysAsync()
    {
        try
        {
            _days = await _files.ListDaysAsync();
            Notice = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Listing the log files failed");
            Notice = $"Couldn't read the logs folder: {e.Message}";
            _days = [];
        }

        var today = _time.GetLocalNow().Date;
        var options = _days
            .Select(d => new ActivityFilterOption<DateOnly?>(
                $"{ActivityViewModel.DayHeader(d.Date.ToDateTime(TimeOnly.MinValue), today)} · {ByteSize.Format(d.Size)}", d.Date))
            .ToList();
        _replacingDays = true;
        Days = options;
        SelectedDay = options.FirstOrDefault(o => o.Value == _date) ?? options.FirstOrDefault();
        _replacingDays = false;
        _date = SelectedDay?.Value;
        await ReadDayAsync();
    }

    /// <summary>Reads the chosen day, cancelling a read still running for another; a failed read leaves the list empty with the reason.</summary>
    private async Task ReadDayAsync()
    {
        _reading?.Cancel();
        var reading = _reading = new CancellationTokenSource();
        Status = null;
        if (_days.FirstOrDefault(d => d.Date == _date) is not { } day)
        {
            _lines = [];
            TruncatedText = null;
            Apply();
            return;
        }

        EmptyText = "Reading the log…";
        try
        {
            var contents = await _files.ReadAsync(day, reading.Token);
            _lines = contents.Events.Select(e => new LogLineViewModel(e, _time.LocalTimeZone)).ToList();
            TruncatedText = contents.Truncated
                ? $"This day's log is very large, so only its last {ByteSize.Format(LogFiles.DEFAULT_MAX_BYTES_PER_FILE)} is shown. Show folder opens the whole file."
                : null;
            Notice = null;
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Reading the log for {Date} failed", day.Date);
            Notice = $"Couldn't read this day's log: {e.Message}";
            _lines = [];
        }
        Apply();
    }

    private void Apply()
    {
        var query = SearchText.Trim();
        Shown = _lines.Where(l => l.Event.Level >= SelectedLevel.Value && l.Matches(query)).ToList();
        if (Selected is not null && !Shown.Contains(Selected))
        {
            Selected = null;
        }

        CountText = Shown.Count == _lines.Count ? Count(_lines.Count) : $"{Shown.Count:N0} of {Count(_lines.Count)} match";
        EmptyText = (_days.Count, _lines.Count, Shown.Count) switch
        {
            (_, _, > 0) => null,
            (0, _, _) => Notice is null ? "No log files yet." : "The logs couldn't be read.",
            (_, 0, _) => Notice is null ? "This day's log is empty." : "This day's log couldn't be read.",
            _ => "No messages match these filters.",
        };
    }

    private static string Count(int messages) => messages == 1 ? "1 message" : $"{messages:N0} messages";
}

/// <summary>One logged message as a row of the Logs tab.</summary>
public sealed class LogLineViewModel(LogEvent logEvent, TimeZoneInfo zone)
{
    /// <summary>Longer messages are shortened in the list; the details pane and Copy keep them whole.</summary>
    internal const int SHORT_LENGTH = 300;

    public LogEvent Event { get; } = logEvent;

    public string TimeText => Event.At is { } at ? TimeZoneInfo.ConvertTime(at, zone).ToString("T", CultureInfo.CurrentCulture) : "";

    public string LevelText => Event.Level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Fatal",
        _ => "Info",
    };

    public bool IsWarning => Event.Level == LogLevel.Warning;

    public bool IsError => Event.Level >= LogLevel.Error;

    /// <summary>The first line, shortened past <see cref="SHORT_LENGTH"/> characters, with a note when a stack trace follows.</summary>
    public string ShortText
    {
        get
        {
            var text = Event.Message.Length > SHORT_LENGTH
                ? $"{Event.Message[..SHORT_LENGTH]}… ({Event.Message.Length:N0} characters)"
                : Event.Message;
            return Event.More is null ? text : $"{text}  (+ details)";
        }
    }

    /// <summary>The whole message and the lines after it, for the details pane.</summary>
    public string FullText => Event.More is null ? Event.Message : $"{Event.Message}\n{Event.More}";

    /// <summary>Whether the message or its following lines contain <paramref name="query"/>; an empty query matches all.</summary>
    internal bool Matches(string query) =>
        query.Length == 0
        || Event.Message.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || (Event.More?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
}
