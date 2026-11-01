using System.Globalization;
using ADA.Core.Settings;
using ADA.Infrastructure.Logging;
using ADA.Infrastructure.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    // Clicking through the retries box saves once at the end, not on every step.
    private static readonly TimeSpan SAVE_DELAY = TimeSpan.FromMilliseconds(300);

    private readonly SettingsStore _store;
    private readonly ArchiveCache _archives;
    private readonly LogFiles? _logs;
    private readonly ILogger _log;
    private readonly bool _loading;
    private AppSettings _settings;
    private CancellationTokenSource? _pendingSave;

    [ObservableProperty]
    public partial bool KeepDownloadedArchives { get; set; }

    /// <summary>Server uploads are read back and hashed; off checks sizes only.</summary>
    [ObservableProperty]
    public partial bool CheckServerUploads { get; set; }

    /// <summary>Retries after a failed download. Decimal because that is what NumericUpDown binds to.</summary>
    [ObservableProperty]
    public partial decimal? DownloadRetries { get; set; }

    /// <summary>Reveals more matching mods while scrolling; turning it off enables page navigation.</summary>
    [ObservableProperty]
    public partial bool EndlessScroll { get; set; }

    /// <summary>Supported page sizes, used only when endless scrolling is off.</summary>
    public IReadOnlyList<int> ModsPageSizes => AppSettings.MODS_PAGE_SIZES;

    /// <summary>Results per page, retained when endless scrolling is enabled.</summary>
    [ObservableProperty]
    public partial int ModsPageSize { get; set; }

    /// <summary>Number of recent releases on the dashboard; a cleared input retains the saved count.</summary>
    [ObservableProperty]
    public partial decimal? DashboardLatestModsCount { get; set; }

    /// <summary>Supported interface scales, as percentages.</summary>
    public IReadOnlyList<int> InterfaceScales => AppSettings.INTERFACE_SCALES;

    /// <summary>Size of the whole interface as a percentage; applied to every window once saved.</summary>
    [ObservableProperty]
    public partial int InterfaceScale { get; set; }

    /// <summary>Themes the page offers: System, Light and Dark.</summary>
    public IReadOnlyList<string> Themes => AppSettings.THEMES;

    /// <summary>Colour theme; System follows the operating system. Applied to every window once saved.</summary>
    [ObservableProperty]
    public partial string Theme { get; set; } = AppSettings.THEME_SYSTEM;

    public int MinDashboardLatestModsCount => AppSettings.MIN_DASHBOARD_LATEST_MODS_COUNT;

    public int MaxDashboardLatestModsCount => AppSettings.MAX_DASHBOARD_LATEST_MODS_COUNT;

    public int MinDownloadRetries => AppSettings.MIN_DOWNLOAD_RETRIES;

    public int MaxDownloadRetries => AppSettings.MAX_DOWNLOAD_RETRIES;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchives))]
    [NotifyCanExecuteChangedFor(nameof(DeleteArchivesCommand))]
    public partial long ArchiveBytes { get; set; }

    [ObservableProperty]
    public partial string ArchiveSummary { get; set; } = "";

    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>The logs folder limits the page offers.</summary>
    public IReadOnlyList<LogLimitOption> LogFolderLimits { get; } =
        AppSettings.LOG_FOLDER_LIMITS.Select(mb => new LogLimitOption(mb)).ToList();

    /// <summary>The logs folder limit; lowering it deletes the oldest logs over it once saved.</summary>
    [ObservableProperty]
    public partial LogLimitOption? LogFolderLimit { get; set; }

    /// <summary>E.g. "Logs use 8.1 MB (7 files)."</summary>
    [ObservableProperty]
    public partial string LogsSummary { get; private set; } = "";

    public string? LogsFolder => _logs?.Folder;

    /// <summary>Whether the page was given the logs folder; the LOGS section is hidden without it.</summary>
    public bool HasLogs => _logs is not null;

    public bool HasArchives => ArchiveBytes > 0;

    public string ArchivesFolder => _archives.Folder;

    /// <summary>Completes when the most recent change has been written; for tests.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <param name="logs">The logs folder, for its limit and usage; null hides the LOGS section.</param>
    public SettingsViewModel(SettingsStore store, ArchiveCache archives, ILogger<SettingsViewModel> log, LogFiles? logs = null)
    {
        _store = store;
        _archives = archives;
        _log = log;
        _logs = logs;

        _loading = true;
        _settings = store.Current;
        KeepDownloadedArchives = _settings.KeepDownloadedArchives;
        CheckServerUploads = _settings.CheckServerUploads;
        DownloadRetries = _settings.DownloadRetries;
        EndlessScroll = _settings.EndlessScroll;
        ModsPageSize = _settings.ModsPageSize;
        DashboardLatestModsCount = _settings.DashboardLatestModsCount;
        InterfaceScale = _settings.InterfaceScale;
        Theme = _settings.Theme;
        LogFolderLimit = LogFolderLimits.First(o => o.Megabytes == _settings.MaxLogFolderMegabytes);
        _loading = false;
    }

    // Saved as soon as it changes; there is no separate "Save" step on this page.
    partial void OnKeepDownloadedArchivesChanged(bool value)
    {
        if (!_loading)
        {
            Update(_settings with { KeepDownloadedArchives = value });
        }
    }

    partial void OnCheckServerUploadsChanged(bool value)
    {
        if (!_loading)
        {
            Update(_settings with { CheckServerUploads = value });
        }
    }

    partial void OnDownloadRetriesChanged(decimal? value)
    {
        // A cleared box is not saved; the last valid value stays in effect.
        if (!_loading && value is { } retries)
        {
            Update(_settings with { DownloadRetries = (int)retries });
        }
    }

    partial void OnEndlessScrollChanged(bool value)
    {
        if (!_loading)
        {
            Update(_settings with { EndlessScroll = value });
        }
    }

    partial void OnModsPageSizeChanged(int value)
    {
        if (!_loading && ModsPageSizes.Contains(value))
        {
            Update(_settings with { ModsPageSize = value });
        }
    }

    partial void OnInterfaceScaleChanged(int value)
    {
        if (!_loading && InterfaceScales.Contains(value))
        {
            Update(_settings with { InterfaceScale = value });
        }
    }

    partial void OnThemeChanged(string value)
    {
        if (!_loading && Themes.Contains(value))
        {
            Update(_settings with { Theme = value });
        }
    }

    partial void OnLogFolderLimitChanged(LogLimitOption? value)
    {
        if (!_loading && value is not null)
        {
            Update(_settings with { MaxLogFolderMegabytes = value.Megabytes });
        }
    }

    private void Update(AppSettings settings)
    {
        _settings = settings;
        _pendingSave?.Cancel();
        _pendingSave = new CancellationTokenSource();
        PendingSave = SaveAsync(settings, _pendingSave.Token);
    }

    partial void OnDashboardLatestModsCountChanged(decimal? value)
    {
        if (!_loading && value is { } count)
        {
            Update(_settings with { DashboardLatestModsCount = (int)Math.Clamp(count,
                MinDashboardLatestModsCount, MaxDashboardLatestModsCount) });
        }
    }

    private async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SAVE_DELAY, ct);
            // Only this page's settings: others (e.g. the Mods page's layout) keep their current values.
            await Task.Run(() => _store.Update(current => current with
            {
                KeepDownloadedArchives = settings.KeepDownloadedArchives,
                CheckServerUploads = settings.CheckServerUploads,
                DownloadRetries = settings.DownloadRetries,
                EndlessScroll = settings.EndlessScroll,
                ModsPageSize = settings.ModsPageSize,
                DashboardLatestModsCount = settings.DashboardLatestModsCount,
                InterfaceScale = settings.InterfaceScale,
                Theme = settings.Theme,
                MaxLogFolderMegabytes = settings.MaxLogFolderMegabytes,
            }), ct);
            ErrorText = null;
            if (_logs is not null)
            {
                await _logs.TrimToAsync(settings.MaxLogFolderMegabytes * 1024L * 1024, ct);
                await MeasureLogsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer change replaced this one.
        }
        catch (Exception e)
        {
            _log.LogError(e, "Saving settings failed");
            ErrorText = $"Couldn't save settings: {e.Message}";
        }
    }

    /// <summary>Re-measures the archive and logs folders; called whenever the page is shown. Never throws.</summary>
    public async Task RefreshAsync()
    {
        await MeasureLogsAsync();
        try
        {
            var (count, bytes) = await Task.Run(_archives.Measure);
            ArchiveBytes = bytes;
            ArchiveSummary = count == 0
                ? "No archives kept."
                : $"{count.ToString("N0", CultureInfo.CurrentCulture)} archive{(count == 1 ? "" : "s")} using {ByteSize.Format(bytes)}.";
        }
        catch (Exception e)
        {
            _log.LogError(e, "Measuring archives failed");
            ArchiveSummary = $"Couldn't measure kept archives: {e.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasArchives))]
    private async Task DeleteArchivesAsync()
    {
        try
        {
            await Task.Run(_archives.DeleteAll);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Deleting archives failed");
            ErrorText = $"Couldn't delete every archive: {e.Message}";
        }
        await RefreshAsync();
    }

    private async Task MeasureLogsAsync()
    {
        if (_logs is null)
        {
            return;
        }
        try
        {
            var (count, bytes) = await Task.Run(_logs.Measure);
            LogsSummary = $"Logs use {ByteSize.Format(bytes)} ({count.ToString("N0", CultureInfo.CurrentCulture)} file{(count == 1 ? "" : "s")}).";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Measuring the logs folder failed");
            LogsSummary = $"Couldn't measure the logs folder: {e.Message}";
        }
    }
}

/// <summary>A logs folder limit as the Settings page lists it: "500 MB", "2 GB".</summary>
public sealed record LogLimitOption(int Megabytes)
{
    public override string ToString() => Megabytes < 1024 ? $"{Megabytes} MB" : $"{Megabytes / 1024} GB";
}
