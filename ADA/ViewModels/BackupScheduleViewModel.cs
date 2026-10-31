using System.ComponentModel;
using ADA.Core.Backups;
using ADA.Core.Games;
using ADA.Core.Settings;
using ADA.Infrastructure.Backups;
using ADA.Infrastructure.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>
/// The Backups page's Schedule box: how often, which install and parts, how many automatic backups to keep,
/// backing up before mod changes, and the backup folder. Each change is saved to settings at once, off the UI
/// thread. Scheduler status arrives on any thread and is posted to the page's thread.
/// </summary>
public partial class BackupScheduleViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<Option<int>> INTERVALS =
    [
        new(0, "Off"),
        new(1, "Every hour"),
        new(3, "Every 3 hours"),
        new(6, "Every 6 hours"),
        new(12, "Every 12 hours"),
        new(24, "Every day"),
        new(168, "Every week"),
    ];

    private readonly SettingsStore _settings;
    private readonly BackupScheduler _scheduler;
    private readonly DashboardViewModel _dashboard;
    private readonly string _defaultFolder;
    private readonly ILogger _log;
    private SynchronizationContext? _viewContext;
    private bool _loading;

    /// <summary>The backup folder changed; the page re-reads it.</summary>
    public event EventHandler? FolderChanged;

    public IReadOnlyList<Option<int>> Intervals => INTERVALS;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn))]
    public partial Option<int> Interval { get; set; } = INTERVALS[0];

    public bool IsOn => Interval.Value > 0;

    /// <summary>The local installs, by display name.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Option<string>> Installs { get; private set; } = [];

    [ObservableProperty]
    public partial Option<string>? Install { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PartOptionViewModel> Parts { get; private set; } = [];

    [ObservableProperty]
    public partial decimal Keep { get; set; } = AppSettings.Default.KeepAutomaticBackups;

    public int MinKeep => AppSettings.MIN_KEEP_AUTOMATIC_BACKUPS;

    public int MaxKeep => AppSettings.MAX_KEEP_AUTOMATIC_BACKUPS;

    [ObservableProperty]
    public partial bool BeforeModChanges { get; set; }

    [ObservableProperty]
    public partial string Folder { get; private set; } = "";

    public bool IsDefaultFolder => _settings.Current.BackupFolder is null;

    /// <summary>"Next backup in 2 h 15 m", a problem, or null when the schedule is off.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Why the folder couldn't be changed, or that old backups stayed behind.</summary>
    [ObservableProperty]
    public partial string? FolderNotice { get; private set; }

    public BackupScheduleViewModel(SettingsStore settings, BackupScheduler scheduler, DashboardViewModel dashboard,
        string defaultFolder, ILogger log)
    {
        (_settings, _scheduler, _dashboard, _defaultFolder, _log) = (settings, scheduler, dashboard, defaultFolder, log);
        scheduler.StatusChanged += (_, _) => OnViewThread(UpdateStatus);
        Load();
    }

    /// <summary>Called by the page each time it's shown: picks up new installs and the scheduler's latest status.</summary>
    internal void Show()
    {
        _viewContext ??= SynchronizationContext.Current;
        Load();
    }

    /// <summary>
    /// Uses <paramref name="folder"/> for backups from now on, creating it; old backups stay where they are. Called by
    /// the view after its folder picker. Not saved when the folder can't be created.
    /// </summary>
    internal void ChangeFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            FolderNotice = $"Couldn't use that folder: {e.Message}";
            return;
        }
        Save(s => s with { BackupFolder = Path.GetFullPath(folder) });
        Folder = Path.GetFullPath(folder);
        FolderNotice = "Backups made before stay in the old folder.";
        OnPropertyChanged(nameof(IsDefaultFolder));
        FolderChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void UseDefaultFolder()
    {
        if (IsDefaultFolder)
        {
            return;
        }
        Save(s => s with { BackupFolder = null });
        Folder = _defaultFolder;
        FolderNotice = "Backups made before stay in the old folder.";
        OnPropertyChanged(nameof(IsDefaultFolder));
        FolderChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIntervalChanged(Option<int> value)
    {
        if (_loading)
        {
            return;
        }
        // Turning the schedule on with no install chosen picks the sidebar's.
        if (value.Value > 0 && Install is null && Installs.FirstOrDefault(i => i.Value == _dashboard.SelectedInstall?.Install.GamePath) is { } selected)
        {
            Install = selected;
        }
        Save(s => s with { BackupIntervalHours = value.Value });
        UpdateStatus();
    }

    partial void OnInstallChanged(Option<string>? value)
    {
        if (_loading)
        {
            return;
        }
        Save(s => s with { BackupInstall = value?.Value });
        LoadParts();
    }

    partial void OnKeepChanged(decimal value)
    {
        if (!_loading)
        {
            Save(s => s with { KeepAutomaticBackups = (int)value });
        }
    }

    partial void OnBeforeModChangesChanged(bool value)
    {
        if (!_loading)
        {
            Save(s => s with { BackupBeforeModChanges = value });
        }
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var settings = _settings.Current;
            Interval = INTERVALS.FirstOrDefault(i => i.Value == settings.BackupIntervalHours) ?? INTERVALS[0];
            Installs = _dashboard.Installs.Where(i => !i.Install.IsRemote)
                .Select(i => new Option<string>(i.Install.GamePath, i.Install.DisplayName)).ToList();
            Install = Installs.FirstOrDefault(i => i.Value == settings.BackupInstall);
            Keep = settings.KeepAutomaticBackups;
            BeforeModChanges = settings.BackupBeforeModChanges;
            Folder = settings.BackupFolder ?? _defaultFolder;
            LoadParts();
        }
        finally
        {
            _loading = false;
        }
        UpdateStatus();
    }

    private void LoadParts()
    {
        foreach (var part in Parts)
        {
            part.PropertyChanged -= OnPartChanged;
        }
        var game = _dashboard.Installs.Select(i => i.Install).FirstOrDefault(i => i.GamePath == Install?.Value);
        var chosen = game is null ? BackupPart.None : BackupScheduler.PartsFor(_settings.Current, game);
        Parts = PartOptionViewModel.For(BackupPart.All, game is null ? BackupPart.None : BackupService.AvailableParts(game));
        foreach (var part in Parts.Where(p => p.IsAvailable))
        {
            part.IsChecked = chosen.HasFlag(part.Part);
        }
        foreach (var part in Parts)
        {
            part.PropertyChanged += OnPartChanged;
        }
    }

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e)
    {
        var names = string.Join(", ", Parts.Where(p => p is { IsAvailable: true, IsChecked: true }).Select(p => p.Part.ToString()));
        Save(s => s with { BackupParts = names });
    }

    private void UpdateStatus()
    {
        StatusText = !IsOn ? null
            : _scheduler.Problem is { } problem ? problem
            : _scheduler.NextDue is { } due ? NextText(due - DateTimeOffset.UtcNow)
            : "The first scheduled backup runs within a minute.";
    }

    /// <summary>"Next backup in 2 h 15 m", "in 5 m", or "Next backup in under a minute".</summary>
    internal static string NextText(TimeSpan wait) => wait.TotalMinutes < 1
        ? "Next backup in under a minute."
        : wait.TotalHours >= 1
            ? $"Next backup in {(int)wait.TotalHours} h {wait.Minutes} m."
            : $"Next backup in {wait.Minutes} m.";

    private void Save(Func<AppSettings, AppSettings> change) => _ = Task.Run(() =>
    {
        try
        {
            _settings.Update(change);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't save the backup schedule");
        }
    });

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
