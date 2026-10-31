using ADA.Core.Backups;
using ADA.Core.Games;
using ADA.Infrastructure.Backups;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>
/// The restore page for one backup and one target install, shown in place of the Backups list. Restores the
/// ticked parts through <see cref="BackupService"/>, which refuses while the game runs, checks the backup
/// first and takes a safety copy. Raises <see cref="Closed"/> when the user goes back, and
/// <see cref="RestartRequested"/> when they choose to restart ADA after restoring its settings.
/// </summary>
public partial class RestoreBackupViewModel : ViewModelBase
{
    private readonly BackupService _backups;
    private readonly GameInstall _target;
    private readonly ILogger _log;

    /// <summary>Raised once when the page should close; the list then re-reads.</summary>
    public event EventHandler? Closed;

    public event EventHandler? RestartRequested;

    public BackupItemViewModel Backup { get; }

    public string Title => $"Restore the backup from {Backup.WhenText}";

    public string TargetText => $"Into {_target.DisplayName}";

    /// <summary>Set when the backup came from another install; null otherwise, and for ADA-settings-only backups.</summary>
    public string? DifferentSourceText { get; }

    public IReadOnlyList<PartOptionViewModel> Parts { get; }

    public string SafetyText =>
        "Before anything is replaced, ADA backs up the same parts as they are now, so this can be undone. "
        + "Files that aren't in the backup are left alone.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool IsDone { get; private set; }

    /// <summary>0 to 1.</summary>
    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    /// <summary>Why the restore didn't happen or stopped.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>What the restore did, once done.</summary>
    [ObservableProperty]
    public partial string? Result { get; private set; }

    /// <summary>ADA settings were restored and wait for a restart.</summary>
    [ObservableProperty]
    public partial bool NeedsRestart { get; private set; }

    public RestoreBackupViewModel(BackupItemViewModel backup, GameInstall target, BackupService backups, ILogger log)
    {
        (Backup, _target, _backups, _log) = (backup, target, backups, log);
        var manifest = backup.Backup.Manifest;
        Parts = PartOptionViewModel.For(manifest.Parts, BackupService.AvailableParts(target), leftUnticked: BackupPart.AdaConfig);
        foreach (var part in Parts)
        {
            part.PropertyChanged += (_, _) => RestoreCommand.NotifyCanExecuteChanged();
        }
        DifferentSourceText = IsFromTarget(manifest) ? null
            : $"This backup was taken from {manifest.Source.DisplayName}. Restoring writes its files into {target.DisplayName} instead.";
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        IsBusy = true;
        Problem = null;
        var parts = PartOptionViewModel.Chosen(Parts);
        var progress = new Progress<BackupProgress>(p =>
        {
            Progress = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = p.Current;
        });
        try
        {
            var result = await _backups.RestoreAsync(Backup.Backup, _target, parts, progress);
            Result = $"Restored {(result.FilesRestored == 1 ? "1 file" : $"{result.FilesRestored} files")}. "
                     + (result.SafetyBackup is { } safety
                         ? $"What they replaced is in {Path.GetFileName(safety.FilePath)}."
                         : "Nothing was there to replace, so no safety copy was needed.")
                     + (result.AdaConfigPending ? " ADA's settings will be restored when ADA restarts." : "");
            NeedsRestart = result.AdaConfigPending;
            IsDone = true;
        }
        catch (Exception e) when (e is BackupException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Restoring {Backup} to {Game} failed", Backup.Backup.FilePath, _target.DisplayName);
            Problem = e.Message;
        }
        finally
        {
            IsBusy = false;
            ProgressText = null;
        }
    }

    private bool CanRestore() => !IsBusy && !IsDone && PartOptionViewModel.Chosen(Parts) != BackupPart.None;

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RestartNow() => RestartRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Same user-data folder and, when the backup recorded one, the same game folder; ADA-only backups have no install.</summary>
    private bool IsFromTarget(BackupManifest manifest) =>
        manifest.Parts == BackupPart.AdaConfig
        || (string.Equals(manifest.Source.UserDataPath, _target.UserDataPath, StringComparison.Ordinal)
            && (manifest.Source.GamePath is null || string.Equals(manifest.Source.GamePath, _target.GamePath, StringComparison.Ordinal)));
}
