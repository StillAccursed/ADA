using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Overlord.Core.Games;
using Overlord.Infrastructure.Installing;

namespace Overlord.ViewModels;

/// <summary>One detected game on the Dashboard, with the state of the mods Overlord manages in it.</summary>
public partial class GameInstallViewModel(GameInstall install, ModInstallService service, ILogger<GameInstallViewModel> log)
    : ViewModelBase
{
    private CheckResult? _lastCheck;

    public GameInstall Install { get; } = install;

    [ObservableProperty]
    public partial string ModsSummary { get; set; } = "Checking mods…";

    /// <summary>Something the user should know: a finished recovery, a failed repair.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    [NotifyPropertyChangedFor(nameof(CanRepair), nameof(HasRepairs))]
    public partial int NeedingRepair { get; set; }

    public bool HasRepairs => NeedingRepair > 0;

    /// <summary>Set by the Dashboard while Satisfactory is running; repairs wait until it closes.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    public partial bool IsGameRunning { get; set; }

    public bool CanRepair => HasRepairs && !IsBusy && !IsGameRunning;

    /// <summary>Launch-time recovery and fast check. Never throws.</summary>
    public async Task CheckAsync()
    {
        IsBusy = true;
        try
        {
            var status = await service.RecoverAndCheckAsync(Install);
            Notice = DescribeRecovery(status);
            Show(status.Check);
        }
        catch (Exception e)
        {
            log.LogError(e, "Checking mods in {Game} failed", Install.GamePath);
            ModsSummary = "Couldn't check mods.";
            Notice = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairAsync()
    {
        if (_lastCheck is null)
            return;

        IsBusy = true;
        Notice = null;
        try
        {
            var progress = new Progress<ModInstallProgress>(p => ProgressText = p.Stage switch
            {
                InstallStage.Downloading when p.Total is > 0 => $"Downloading {p.Reference}: {100 * p.Done / p.Total.Value}%",
                InstallStage.Downloading => $"Downloading {p.Reference}…",
                InstallStage.Extracting => $"Repairing {p.Reference}: {p.Done} of {p.Total} files",
                _ => $"Finishing {p.Reference}…",
            });
            var result = await service.RepairAsync(Install, _lastCheck, progress);

            var parts = new List<string>();
            if (result.Repaired.Count > 0)
                parts.Add($"Repaired {string.Join(", ", result.Repaired)}.");
            parts.AddRange(result.Failed.Select(f => $"Couldn't repair {f.Key}: {f.Value}"));
            Notice = string.Join(" ", parts);

            Show((await service.RecoverAndCheckAsync(Install)).Check);
        }
        catch (Exception e)
        {
            // Offline, the game started, a disk error: all shown here rather than crashing the app.
            if (e is not GameRunningException)
                log.LogError(e, "Repair in {Game} failed", Install.GamePath);
            Notice = e is GameRunningException ? e.Message : $"Couldn't repair: {e.Message}";
        }
        finally
        {
            ProgressText = null;
            IsBusy = false;
        }
    }

    private void Show(CheckResult check)
    {
        _lastCheck = check;
        var broken = check.NeedingRepair.Select(m => m.Reference).ToList();
        NeedingRepair = broken.Count;

        var parts = new List<string>
        {
            check.Mods.Count switch
            {
                0 => "No mods installed by Overlord yet",
                1 => "1 mod installed",
                var n => $"{n} mods installed",
            },
        };
        if (check.Mods.Count > 0)
        {
            parts.Add(broken.Count == 0
                ? "all files OK"
                : $"{broken.Count} need{(broken.Count == 1 ? "s" : "")} repair: {string.Join(", ", broken)}");
        }
        if (check.Unmanaged.Count > 0)
            parts.Add($"{check.Unmanaged.Count} folder{(check.Unmanaged.Count == 1 ? "" : "s")} not managed by Overlord");
        ModsSummary = string.Join(" · ", parts);
    }

    private static string? DescribeRecovery(GameModsStatus status)
    {
        if (status.RecoveryDeferred)
            return "An install was interrupted. Close Satisfactory and Overlord will finish it.";
        if (!status.Recovery.HadInterruptedTransaction)
            return null;

        var r = status.Recovery;
        var parts = new List<string> { "Finished an interrupted install." };
        if (r.RolledForward.Count > 0)
            parts.Add($"Completed: {string.Join(", ", r.RolledForward)}.");
        if (r.Discarded.Count > 0)
            parts.Add($"Not installed (wasn't verified yet): {string.Join(", ", r.Discarded)}.");
        if (r.RolledBack.Count > 0)
            parts.Add($"Put back as they were: {string.Join(", ", r.RolledBack)}.");
        return string.Join(" ", parts);
    }
}
