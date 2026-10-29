using ADA.Core.Games;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>One detected game on the Dashboard, with the state of the mods ADA manages in it.</summary>
public partial class GameInstallViewModel(GameInstall install, ModInstallService service, ModResolutionService resolution,
    GameProfiles profiles, ILogger<GameInstallViewModel> log)
    : ViewModelBase
{
    private CheckResult? _lastCheck;
    private IReadOnlyList<FoundMod> _found = [];
    private ModChangePlan? _fix;

    /// <summary>Prevents another UI mutation while the captured profile switch is in progress.</summary>
    [ObservableProperty]
    internal partial bool IsSwitchingProfile { get; set; }

    /// <summary>Includes waiting requests on the Mods page, so profile switching cannot overtake them.</summary>
    [ObservableProperty]
    internal partial bool HasModOperations { get; set; }

    public GameInstall Install { get; } = install;

    [ObservableProperty]
    public partial string ModsSummary { get; set; } = "Checking mods…";

    /// <summary>Something the user should know: a finished recovery, a failed repair.</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand), nameof(ImportToCurrentCommand), nameof(CreateNewProfileCommand), nameof(FixCommand))]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    [NotifyPropertyChangedFor(nameof(CanRepair), nameof(HasRepairs))]
    public partial int NeedingRepair { get; set; }

    public bool HasRepairs => NeedingRepair > 0;

    /// <summary>Set by the Dashboard while Satisfactory is running; repairs wait until it closes.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand), nameof(FixCommand))]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    public partial bool IsGameRunning { get; set; }

    public bool CanRepair => HasRepairs && !IsBusy && !IsGameRunning;

    /// <summary>The game's active profile; null until the user creates or chooses one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportToCurrentText))]
    [NotifyCanExecuteChangedFor(nameof(ImportToCurrentCommand))]
    public partial string? ProfileName { get; set; }

    // Mods another manager installed: the user chooses what to do with them, in steps.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFoundMods), nameof(ShowFoundChoice), nameof(ShowNewProfile))]
    [NotifyCanExecuteChangedFor(nameof(ImportToCurrentCommand), nameof(CreateNewProfileCommand))]
    public partial FoundModsStep FoundStep { get; set; }

    public bool HasFoundMods => FoundStep != FoundModsStep.None;

    public bool ShowFoundChoice => FoundStep == FoundModsStep.Choose;

    public bool ShowNewProfile => FoundStep == FoundModsStep.NewProfile;


    [ObservableProperty]
    public partial string FoundTitle { get; set; } = "";

    [ObservableProperty]
    public partial string FoundNames { get; set; } = "";

    [ObservableProperty]
    public partial string NewProfileName { get; set; } = "Imported mods";

    [ObservableProperty]
    public partial bool SwitchToNewProfile { get; set; } = true;

    [ObservableProperty]
    public partial string? NewProfileProblem { get; set; }

    /// <summary>What the installed mods are missing, or why that couldn't be worked out; null when nothing is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixProblem))]
    public partial string? FixProblem { get; set; }

    public bool HasFixProblem => FixProblem is not null;

    /// <summary>A fix is ready to apply: missing dependencies to install, mods to update.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FixCommand))]
    public partial bool HasFix { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> FixLines { get; set; } = [];

    /// <summary>Newer versions of installed mods that work in this game; the Dashboard's nav button shows an arrow.</summary>
    [ObservableProperty]
    public partial bool HasUpdates { get; set; }

    [ObservableProperty]
    public partial string UpdatesTitle { get; set; } = "";

    public string ImportToCurrentText => ProfileName is null ? "Add to current profile" : $"Add to \"{ProfileName}\"";

    /// <summary>
    /// Launch-time recovery and fast check. Skipped while this card or another operation holds the
    /// installation; that operation re-checks when it finishes. Never throws.
    /// </summary>
    public async Task CheckAsync()
    {
        if (IsBusy || IsSwitchingProfile || profiles.IsChanging(Install))
        {
            return;
        }
        IsBusy = true;
        try
        {
            var status = await service.RecoverAndCheckAsync(Install);
            if (!status.RecoveryDeferred)
            {
                await profiles.ReconcileAsync(Install);
            }
            Notice = DescribeRecovery(status);
            Show(status.Check);
            ProfileName = await profiles.ActiveNameAsync(Install);
            ShowFound(await profiles.FindUnmanagedAsync(Install));
            await FindFixesAsync();
            await FindUpdatesAsync();
        }
        catch (InstallationBusyException)
        {
            // Another operation started between the guard above and the reservation.
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
        {
            return;
        }

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
            {
                parts.Add($"Repaired {string.Join(", ", result.Repaired)}.");
            }

            parts.AddRange(result.Failed.Select(f => $"Couldn't repair {f.Key}: {f.Value}"));
            Notice = string.Join(" ", parts);

            Show((await service.RecoverAndCheckAsync(Install)).Check);
        }
        catch (Exception e)
        {
            // Offline, the game started, a disk error: all shown here rather than crashing the app.
            if (e is not GameRunningException)
            {
                log.LogError(e, "Repair in {Game} failed", Install.GamePath);
            }

            Notice = e is GameRunningException ? e.Message : $"Couldn't repair: {e.Message}";
        }
        finally
        {
            ProgressText = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportToCurrent))]
    private Task ImportToCurrentAsync() => ActOnFoundAsync(async () =>
    {
        var result = await profiles.ImportToCurrentAsync(Install, _found);
        return Imported(result);
    });

    [RelayCommand]
    private void ChooseNewProfile()
    {
        NewProfileProblem = null;
        FoundStep = FoundModsStep.NewProfile;
    }

    [RelayCommand(CanExecute = nameof(CanActOnFound))]
    private Task CreateNewProfileAsync()
    {
        if (profiles.ProblemWithNewName(NewProfileName) is { } problem)
        {
            NewProfileProblem = problem;
            return Task.CompletedTask;
        }
        return ActOnFoundAsync(async () =>
        {
            var result = await profiles.ImportToNewAsync(Install, _found, NewProfileName, SwitchToNewProfile);
            ProfileName = await profiles.ActiveNameAsync(Install);
            return SwitchToNewProfile
                ? Imported(result)
                : $"Saved them as the profile \"{result.Profile.Name}\". They stay in the Mods folder as they are, and ADA won't ask about them again.";
        });
    }

    [RelayCommand]
    private void BackToFoundChoice() => FoundStep = FoundModsStep.Choose;

    private bool CanActOnFound => HasFoundMods && !IsBusy;

    private bool CanImportToCurrent => CanActOnFound && ProfileName is not null;

    private bool CanFix => HasFix && !IsBusy && !IsGameRunning;

    [RelayCommand(CanExecute = nameof(CanFix))]
    private async Task FixAsync()
    {
        if (_fix is not { } plan)
        {
            return;
        }

        IsBusy = true;
        Notice = null;
        try
        {
            var progress = new Progress<ModInstallProgress>(p => ProgressText = PlanText.Progress(p));
            var result = await resolution.ApplyAsync(Install, plan, progress);
            await profiles.RecordInstalledAsync(Install);
            Notice = $"Installed {string.Join(", ", result.Installed.Select(m => $"{m.Reference} {m.Version}"))}.";
            Show((await service.RecoverAndCheckAsync(Install)).Check);
        }
        catch (Exception e)
        {
            if (e is not GameRunningException)
            {
                log.LogError(e, "Fixing mods in {Game} failed", Install.GamePath);
            }

            Notice = e is GameRunningException ? e.Message : $"Nothing was changed: {e.Message}";
        }
        finally
        {
            ProgressText = null;
            IsBusy = false;
        }
        await FindFixesAsync();
    }

    /// <summary>
    /// Looks for newer versions of the installed mods that work together in this game (never installs
    /// them). Run after the launch check and each catalogue refresh. Never throws.
    /// </summary>
    public async Task FindUpdatesAsync()
    {
        try
        {
            var plan = await resolution.PlanUpdateAsync(Install);
            var updates = plan.Succeeded ? plan.Install.Count(i => i.IsUpdate) : 0;
            UpdatesTitle = updates == 1 ? "1 mod update available" : $"{updates} mod updates available";
            HasUpdates = updates > 0;
        }
        catch (Exception e)
        {
            // Offline: keep what was found last time, and try again on the next check.
            log.LogWarning(e, "Couldn't look for updates in {Game}", Install.GamePath);
        }
    }

    [RelayCommand]
    private void DismissFix()
    {
        HasFix = false;
        FixProblem = null;
    }

    [RelayCommand]
    private void DismissNotice()
    {
        Notice = null;
    }

    /// <summary>
    /// Looks for what the installed mods need (a quick local look first, the resolver only if
    /// something is missing), so mods imported from another manager work. Never throws.
    /// </summary>
    private async Task FindFixesAsync()
    {
        _fix = null;
        HasFix = false;
        FixProblem = null;
        try
        {
            var missing = await Task.Run(() => service.InstallerFor(Install).MissingDependencies());
            if (missing.Count == 0)
            {
                return;
            }

            var plan = await resolution.PlanFixAsync(Install);
            if (!plan.Succeeded)
            {
                FixProblem = "Some mods need others that aren't installed, but ADA can't install them: "
                             + string.Join(" ", plan.Problems);
                return;
            }
            if (plan.ChangesNothing)
            {
                return;
            }

            _fix = plan;
            FixLines = PlanText.Lines(plan);
            HasFix = true;
        }
        catch (Exception e)
        {
            // Offline on launch, say: try again on the next check rather than nag.
            log.LogWarning(e, "Couldn't work out what mods in {Game} need", Install.GamePath);
        }
    }

    private async Task ActOnFoundAsync(Func<Task<string>> act)
    {
        IsBusy = true;
        Notice = null;
        try
        {
            Notice = await act();
            FoundStep = FoundModsStep.None;
            Show((await service.RecoverAndCheckAsync(Install)).Check);
            ShowFound(await profiles.FindUnmanagedAsync(Install));
            await FindFixesAsync();
        }
        catch (Exception e)
        {
            if (e is not GameRunningException and not InvalidProfileException)
            {
                log.LogError(e, "Handling found mods in {Game} failed", Install.GamePath);
            }

            Notice = e is GameRunningException or InvalidProfileException ? e.Message : $"Nothing was changed: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowFound(FoundMods found)
    {
        _found = found.Mods;
        if (_found.Count == 0)
        {
            FoundStep = FoundModsStep.None;
            return;
        }

        FoundTitle = _found.Count == 1
            ? "Found 1 mod installed by another mod manager"
            : $"Found {_found.Count} mods installed by another mod manager";
        FoundNames = string.Join(", ", _found.Select(m => m.FriendlyName ?? m.Reference).Order(StringComparer.CurrentCultureIgnoreCase));
        FoundStep = FoundModsStep.Choose;
    }

    private static string Imported(FoundModsImport result)
    {
        var text = $"Added {result.Adopted.Count} mod{(result.Adopted.Count == 1 ? "" : "s")} to \"{result.Profile.Name}\". Their files weren't changed.";
        return result.NotInProfile.Count == 0
            ? text
            : $"{text} Not listed in the profile, as their versions aren't exact: {string.Join(", ", result.NotInProfile)}.";
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
                0 => "No mods installed by ADA yet",
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
        {
            parts.Add($"{check.Unmanaged.Count} folder{(check.Unmanaged.Count == 1 ? "" : "s")} not managed by ADA");
        }

        ModsSummary = string.Join(" · ", parts);
    }

    private static string? DescribeRecovery(GameModsStatus status)
    {
        if (status.RecoveryDeferred)
        {
            return "An install was interrupted. Close Satisfactory and ADA will finish it.";
        }

        if (!status.Recovery.HadInterruptedTransaction)
        {
            return null;
        }

        var r = status.Recovery;
        var parts = new List<string> { "Finished an interrupted install." };
        if (r.RolledForward.Count > 0)
        {
            parts.Add($"Completed: {string.Join(", ", r.RolledForward)}.");
        }

        if (r.Discarded.Count > 0)
        {
            parts.Add($"Not installed (wasn't verified yet): {string.Join(", ", r.Discarded)}.");
        }

        if (r.RolledBack.Count > 0)
        {
            parts.Add($"Put back as they were: {string.Join(", ", r.RolledBack)}.");
        }

        return string.Join(" ", parts);
    }
}

/// <summary>Steps of the Dashboard's "mods found from another manager" prompt.</summary>
public enum FoundModsStep
{
    /// <summary>No prompt is shown.</summary>
    None,
    /// <summary>Asking whether to add the mods to the current profile or a new one.</summary>
    Choose,
    /// <summary>Naming the new profile.</summary>
    NewProfile,
}
