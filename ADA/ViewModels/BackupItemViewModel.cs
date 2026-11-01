using ADA.Core.Backups;
using ADA.Infrastructure.Backups;

namespace ADA.ViewModels;

/// <summary>One backup in the Backups page's list. Immutable: a re-read builds new items.</summary>
public sealed class BackupItemViewModel(BackupInfo backup)
{
    public BackupInfo Backup { get; } = backup;

    public string WhenText => SaveText.When(Backup.Manifest.CreatedUtc);

    public string TriggerText => Backup.Manifest.Trigger switch
    {
        BackupTrigger.Manual => "Made by hand",
        BackupTrigger.Scheduled => "Scheduled",
        BackupTrigger.BeforeModChange => "Before a mod change",
        BackupTrigger.BeforeRestore => "Safety copy taken before a restore",
        _ => Backup.Manifest.Trigger.ToString(),
    };

    public string SourceText => Backup.Manifest.Source.DisplayName;

    public string PartsText => BackupService.DescribeParts(Backup.Manifest.Parts);

    public string DetailText
    {
        get
        {
            var files = Backup.Manifest.Files.Count == 1 ? "1 file" : $"{Backup.Manifest.Files.Count} files";
            return $"{PartsText} · {files} · {ByteSize.Format(Backup.Size)}";
        }
    }

    public bool HasSkipped => Backup.Manifest.Skipped.Count > 0;

    public string SkippedText => Backup.Manifest.Skipped.Count == 1
        ? "1 file was left out because it kept changing while being copied."
        : $"{Backup.Manifest.Skipped.Count} files were left out because they kept changing while being copied.";
}
