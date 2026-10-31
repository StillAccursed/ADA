using System.Collections.ObjectModel;
using System.ComponentModel;
using ADA.Core.Backups;
using ADA.Core.Games;
using ADA.Infrastructure.Backups;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>
/// The Backups page: backing up the sidebar's install now, the list of backups of every install, deleting
/// them, and the restore page in the list's place. Re-reads the list each time it's shown and after every
/// backup, delete or restore.
/// </summary>
public partial class BackupsViewModel : ViewModelBase
{
    private readonly BackupService _backups;
    private readonly DashboardViewModel _dashboard;
    private readonly ILogger _log;
    private CancellationTokenSource? _running;

    /// <summary>The user asked to restart ADA after restoring its settings; <c>App</c> starts a new ADA and closes this one.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>Newest first.</summary>
    public ObservableCollection<BackupItemViewModel> Backups { get; } = [];

    public bool HasBackups => Backups.Count > 0;

    /// <summary>The sidebar install's parts, for Back up now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand))]
    public partial IReadOnlyList<PartOptionViewModel> Parts { get; private set; } = [];

    public string InstallText => _dashboard.SelectedInstall?.Install.DisplayName ?? "No install selected";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    /// <summary>What the last backup or delete did, or why it didn't.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    /// <summary>Files in the backup folder that aren't readable backups; null when there are none.</summary>
    [ObservableProperty]
    public partial string? UnreadableText { get; private set; }

    /// <summary>A question for the page's view to show as a dialog.</summary>
    [ObservableProperty]
    public partial ConfirmViewModel? Confirmation { get; private set; }

    /// <summary>The restore page, in place of the list; null while the list shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRestoring))]
    public partial RestoreBackupViewModel? Restoring { get; private set; }

    public bool IsRestoring => Restoring is not null;

    public string FolderPath => _backups.Folder;

    /// <summary>The Schedule box.</summary>
    public BackupScheduleViewModel Schedule { get; }

    /// <summary>The most recent re-read; for tests.</summary>
    internal Task PendingRefresh { get; private set; } = Task.CompletedTask;

    public BackupsViewModel(BackupService backups, DashboardViewModel dashboard, BackupScheduleViewModel schedule,
        ILogger<BackupsViewModel> log)
    {
        (_backups, _dashboard, Schedule, _log) = (backups, dashboard, schedule, log);
        // A new folder holds different backups.
        schedule.FolderChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(FolderPath));
            _ = RefreshAsync();
        };
        dashboard.PropertyChanged += OnDashboardChanged;
        UpdateParts();
    }

    /// <summary>Called by the view each time the page is shown.</summary>
    internal Task ShowAsync()
    {
        UpdateParts();
        Schedule.Show();
        return RefreshAsync();
    }

    /// <summary>Re-reads the backup folder. Never throws.</summary>
    internal Task RefreshAsync()
    {
        PendingRefresh = ReadAsync();
        return PendingRefresh;
    }

    [RelayCommand(CanExecute = nameof(CanBackUp))]
    private async Task BackUpNowAsync()
    {
        if (_dashboard.SelectedInstall?.Install is not { } game)
        {
            return;
        }

        var parts = PartOptionViewModel.Chosen(Parts);
        IsBusy = true;
        Notice = null;
        _running = new CancellationTokenSource();
        var progress = new Progress<BackupProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = p.Current;
        });
        try
        {
            var backup = await _backups.CreateAsync(game, parts, BackupTrigger.Manual, progress, _running.Token);
            var skipped = backup.Manifest.Skipped.Count == 0 ? "" : $" {new BackupItemViewModel(backup).SkippedText}";
            Notice = $"Backed up {BackupItemViewModel.PartsLabel(parts)} ({ByteSize.Format(backup.Size)}).{skipped}";
        }
        catch (OperationCanceledException)
        {
            Notice = "Backup cancelled; nothing was kept.";
        }
        catch (Exception e) when (e is BackupException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Backing up {Game} failed", game.DisplayName);
            Notice = $"Couldn't back up: {e.Message}";
        }
        finally
        {
            _running.Dispose();
            _running = null;
            IsBusy = false;
            ProgressText = null;
        }
        await RefreshAsync();
    }

    private bool CanBackUp() =>
        !IsBusy && _dashboard.SelectedInstall is not null && PartOptionViewModel.Chosen(Parts) != BackupPart.None;

    [RelayCommand]
    private void Cancel() => _running?.Cancel();

    [RelayCommand]
    private async Task DeleteAsync(BackupItemViewModel item)
    {
        var question = new ConfirmViewModel("Delete this backup?",
            $"The backup from {item.WhenText} ({item.DetailText}) will be deleted. This can't be undone.", "Delete")
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
            await _backups.DeleteAsync(item.Backup);
            Notice = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Deleting {Backup} failed", item.Backup.FilePath);
            Notice = $"Couldn't delete the backup: {e.Message}";
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void Restore(BackupItemViewModel item)
    {
        if (_dashboard.SelectedInstall?.Install is not { } game)
        {
            Notice = "Choose an install in the sidebar to restore into.";
            return;
        }

        var page = new RestoreBackupViewModel(item, game, _backups, _log);
        page.Closed += OnRestoreClosed;
        page.RestartRequested += (_, _) => RestartRequested?.Invoke(this, EventArgs.Empty);
        Restoring = page;
    }

    private async Task ReadAsync()
    {
        try
        {
            var listing = await _backups.ListAsync();
            Backups.Clear();
            foreach (var backup in listing.Backups)
            {
                Backups.Add(new BackupItemViewModel(backup));
            }
            UnreadableText = listing.Unreadable.Count switch
            {
                0 => null,
                1 => "1 file in the backup folder isn't a readable backup.",
                var count => $"{count} files in the backup folder aren't readable backups.",
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Reading backups in {Folder} failed", _backups.Folder);
            Notice = $"Couldn't read the backup folder: {e.Message}";
        }
        OnPropertyChanged(nameof(HasBackups));
    }

    private void OnRestoreClosed(object? sender, EventArgs e)
    {
        Restoring = null;
        _ = RefreshAsync();
    }

    private void UpdateParts()
    {
        foreach (var part in Parts)
        {
            part.PropertyChanged -= OnPartChanged;
        }
        Parts = _dashboard.SelectedInstall?.Install is { } game
            ? PartOptionViewModel.For(BackupPart.All, BackupService.AvailableParts(game))
            : [];
        foreach (var part in Parts)
        {
            part.PropertyChanged += OnPartChanged;
        }
        OnPropertyChanged(nameof(InstallText));
    }

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e) => BackUpNowCommand.NotifyCanExecuteChanged();

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.SelectedInstall))
        {
            UpdateParts();
        }
    }
}
