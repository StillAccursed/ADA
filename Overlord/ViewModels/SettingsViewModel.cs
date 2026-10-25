using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Overlord.Core.Settings;
using Overlord.Infrastructure.Settings;

namespace Overlord.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    // Clicking through the retries box saves once at the end, not on every step.
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly SettingsStore _store;
    private readonly ArchiveCache _archives;
    private readonly ILogger _log;
    private readonly bool _loading;
    private AppSettings _settings;
    private CancellationTokenSource? _pendingSave;

    public SettingsViewModel(SettingsStore store, ArchiveCache archives, ILogger<SettingsViewModel> log)
    {
        _store = store;
        _archives = archives;
        _log = log;

        _loading = true;
        _settings = store.Current;
        KeepDownloadedArchives = _settings.KeepDownloadedArchives;
        DownloadRetries = _settings.DownloadRetries;
        _loading = false;
    }

    [ObservableProperty]
    public partial bool KeepDownloadedArchives { get; set; }

    /// <summary>Retries after a failed download. Decimal because that is what NumericUpDown binds to.</summary>
    [ObservableProperty]
    public partial decimal? DownloadRetries { get; set; }

    public int MinDownloadRetries => AppSettings.MinDownloadRetries;

    public int MaxDownloadRetries => AppSettings.MaxDownloadRetries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchives))]
    [NotifyCanExecuteChangedFor(nameof(DeleteArchivesCommand))]
    public partial long ArchiveBytes { get; set; }

    [ObservableProperty]
    public partial string ArchiveSummary { get; set; } = "";

    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    public bool HasArchives => ArchiveBytes > 0;

    public string ArchivesFolder => _archives.Folder;

    /// <summary>Completes when the most recent change has been written; for tests.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    // Saved as soon as it changes; there is no separate "Save" step on this page.
    partial void OnKeepDownloadedArchivesChanged(bool value)
    {
        if (!_loading)
            Update(_settings with { KeepDownloadedArchives = value });
    }

    partial void OnDownloadRetriesChanged(decimal? value)
    {
        // A cleared box is not saved; the last valid value stays in effect.
        if (!_loading && value is { } retries)
            Update(_settings with { DownloadRetries = (int)retries });
    }

    private void Update(AppSettings settings)
    {
        _settings = settings;
        _pendingSave?.Cancel();
        _pendingSave = new CancellationTokenSource();
        PendingSave = SaveAsync(settings, _pendingSave.Token);
    }

    private async Task SaveAsync(AppSettings settings, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SaveDelay, ct);
            // Only this page's settings: others (e.g. the Mods page's layout) keep their current values.
            await Task.Run(() => _store.Update(current => current with
            {
                KeepDownloadedArchives = settings.KeepDownloadedArchives,
                DownloadRetries = settings.DownloadRetries,
            }), ct);
            ErrorText = null;
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

    /// <summary>Re-measures the archive folder; called whenever the page is shown. Never throws.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var (count, bytes) = await Task.Run(_archives.Measure);
            ArchiveBytes = bytes;
            ArchiveSummary = count == 0
                ? "No archives kept."
                : $"{count.ToString("N0", CultureInfo.CurrentCulture)} archive{(count == 1 ? "" : "s")} using {FormatSize(bytes)}.";
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

    internal static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} bytes"
            : $"{size.ToString(size < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
