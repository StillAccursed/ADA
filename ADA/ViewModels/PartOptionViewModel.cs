using ADA.Core.Backups;
using ADA.Infrastructure.Backups;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ADA.ViewModels;

/// <summary>One part's checkbox, when backing up or restoring; greyed out with a reason when the install can't have it.</summary>
public partial class PartOptionViewModel(BackupPart part, string? unavailableReason, bool isChecked) : ViewModelBase
{
    public BackupPart Part { get; } = part;

    public string Label => BackupService.Describe(Part);

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>Why the part can't be chosen; null when it can.</summary>
    public string? UnavailableReason { get; } = unavailableReason;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    /// <summary>
    /// A checkbox for each part in <paramref name="offered"/>; parts outside <paramref name="available"/> are
    /// greyed out and unticked, the rest ticked unless they're in <paramref name="leftUnticked"/>.
    /// </summary>
    internal static IReadOnlyList<PartOptionViewModel> For(BackupPart offered, BackupPart available, BackupPart leftUnticked = BackupPart.None) =>
        BackupService.Split(offered)
            .Select(part => available.HasFlag(part)
                ? new PartOptionViewModel(part, null, !leftUnticked.HasFlag(part))
                : new PartOptionViewModel(part, WhyUnavailable(part), false))
            .ToList();

    /// <summary>The ticked, available parts together.</summary>
    internal static BackupPart Chosen(IEnumerable<PartOptionViewModel> options) =>
        options.Where(o => o is { IsAvailable: true, IsChecked: true }).Aggregate(BackupPart.None, (all, o) => all | o.Part);

    private static string WhyUnavailable(BackupPart part) => part == BackupPart.ModConfig
        ? "Mod settings on dedicated servers can't be backed up yet."
        : "ADA doesn't know where this install keeps its saves and settings.";
}
