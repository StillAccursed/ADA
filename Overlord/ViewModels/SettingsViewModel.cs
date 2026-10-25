using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overlord.Core.Settings;
using Overlord.Infrastructure.Settings;

namespace Overlord.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore _store;
    private readonly ArchiveCache _archives;
    private bool _loading;

    public SettingsViewModel(SettingsStore store, ArchiveCache archives)
    {
        _store = store;
        _archives = archives;

        _loading = true;
        var settings = store.Load();
        KeepDownloadedArchives = settings.KeepDownloadedArchives;
        DownloadRetries = settings.DownloadRetries;
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

    public bool HasArchives => ArchiveBytes > 0;

    public string ArchivesFolder => _archives.Folder;

    // Saved as soon as it changes; there is no separate "Save" step on this page.
    partial void OnKeepDownloadedArchivesChanged(bool value)
    {
        if (!_loading)
            _store.Save(_store.Load() with { KeepDownloadedArchives = value });
    }

    partial void OnDownloadRetriesChanged(decimal? value)
    {
        // A cleared box is not saved; the last valid value stays in effect.
        if (!_loading && value is { } retries)
            _store.Save(_store.Load() with { DownloadRetries = (int)retries });
    }

    /// <summary>Re-measures the archive folder; called whenever the page is shown.</summary>
    public async Task RefreshAsync()
    {
        var (count, bytes) = await Task.Run(_archives.Measure);
        ArchiveBytes = bytes;
        ArchiveSummary = count == 0
            ? "No archives kept."
            : $"{count.ToString("N0", CultureInfo.CurrentCulture)} archive{(count == 1 ? "" : "s")} using {FormatSize(bytes)}.";
    }

    [RelayCommand(CanExecute = nameof(HasArchives))]
    private async Task DeleteArchivesAsync()
    {
        await Task.Run(_archives.DeleteAll);
        await RefreshAsync();
    }

    private static string FormatSize(long bytes)
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
