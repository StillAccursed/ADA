using ADA.Core.Profiles;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADA.ViewModels;

/// <summary>
/// The "Switch profile" dialog: works out what switching one installation to a saved profile would
/// change, with the profile's versions and with newer ones, shows the chosen option, and applies it
/// only when the user chooses Switch. The profile's versions are chosen by default unless they can't
/// be installed. Applying re-plans under the installation's reservation with the same choice; if the
/// result differs from what is shown, nothing changes and the new result is shown instead. Never
/// throws; problems are shown in the dialog.
/// </summary>
public partial class SwitchProfileViewModel : ViewModelBase
{
    private readonly GameInstallViewModel _game;
    private readonly ModProfile _profile;
    private readonly GameProfiles _profiles;
    private readonly ModResolutionService _resolution;
    private readonly Func<string, string, string, Task<string>> _changelog;
    private readonly Func<bool> _canSwitch;
    private ProfileSwitchPreview? _preview;

    /// <summary>Raised on the UI thread once the dialog should close: true after a switch, false otherwise.</summary>
    public event EventHandler<bool>? Closed;

    public string Title { get; }

    public string SavedOptionText { get; }

    /// <summary>Reassures that the outgoing profile keeps its mods; null when the game has no active profile.</summary>
    public string? KeepsNote { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand), nameof(CancelCommand))]
    public partial bool IsWorking { get; private set; } = true;

    /// <summary>The chosen option is worked out and can be applied.</summary>
    public bool IsReady => !IsWorking && SelectedPlan is { Succeeded: true };

    /// <summary>Newer versions exist, so the dialog asks which to use.</summary>
    [ObservableProperty]
    public partial bool OffersChoice { get; private set; }

    /// <summary>The user chose newer versions over the profile's; false by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSavedVersions), nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    public partial bool UseNewerVersions { get; set; }

    public bool UseSavedVersions
    {
        get => !UseNewerVersions;
        set => UseNewerVersions = !value;
    }

    /// <summary>False when the profile's versions can't be installed; the newer option is then the only one.</summary>
    [ObservableProperty]
    public partial bool CanUseSavedVersions { get; private set; } = true;

    /// <summary>Why the profile's versions can't be installed, shown under the disabled option.</summary>
    [ObservableProperty]
    public partial string? SavedVersionsProblem { get; private set; }

    [ObservableProperty]
    public partial string NewerOptionText { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<NewerVersionViewModel> NewerVersions { get; private set; } = [];

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Working out the switch…";

    [ObservableProperty]
    public partial SwitchChanges? Changes { get; private set; }

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    /// <summary>Why the switch can't happen, what changed since the preview, or why it failed.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    private ModChangePlan? SelectedPlan => UseNewerVersions ? _preview?.Newest : _preview?.Saved;

    private bool CanApply => IsReady && _canSwitch();

    private bool CanCancel => !IsWorking;

    /// <param name="activeName">The installation's current profile, if any.</param>
    /// <param name="changelog">Changelog text for a mod between two versions (reference, from, to).</param>
    /// <param name="canSwitch">Whether the installation may change now (game closed, nothing else running).</param>
    public SwitchProfileViewModel(GameInstallViewModel game, ModProfile profile, string? activeName, GameProfiles profiles,
        ModResolutionService resolution, Func<string, string, string, Task<string>> changelog, Func<bool> canSwitch)
    {
        _game = game;
        _profile = profile;
        _profiles = profiles;
        _resolution = resolution;
        _changelog = changelog;
        _canSwitch = canSwitch;
        Title = $"Switch {game.Install.DisplayName} to \"{profile.Name}\"?";
        SavedOptionText = $"Use the profile's versions: installs exactly what \"{profile.Name}\" saved";
        KeepsNote = activeName is null ? null : $"The current profile, \"{activeName}\", keeps its mods, so you can switch back.";
    }

    /// <summary>Works out both options; called once when the dialog opens.</summary>
    public async Task PreviewAsync()
    {
        IsWorking = true;
        try
        {
            Show(await _profiles.PreviewSwitchAsync(_game.Install, _profile, _resolution));
        }
        catch (Exception e)
        {
            Show(new ProfileSwitchPreview(new ModChangePlan([], [], [], 0, [$"Couldn't work out the switch: {e.Message}"]), null));
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Re-checks whether Switch is available, e.g. after the game starts or stops.</summary>
    public void RefreshCanSwitch() => SwitchCommand.NotifyCanExecuteChanged();

    partial void OnUseNewerVersionsChanged(bool value) => ShowSelectedPlan();

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task SwitchAsync()
    {
        IsWorking = true;
        Problem = null;
        try
        {
            var progress = new Progress<ModInstallProgress>(p => ProgressText = PlanText.Progress(p));
            await _profiles.SwitchAsync(_game.Install, _profile, _resolution, progress,
                useNewerVersions: UseNewerVersions, expected: SelectedPlan);
            _game.ProfileName = _profile.Name;
            Closed?.Invoke(this, true);
        }
        catch (PlanChangedException changed)
        {
            // Keep the user's choice; only the chosen option's plan is replaced.
            _preview = UseNewerVersions ? _preview! with { Newest = changed.Plan } : _preview! with { Saved = changed.Plan };
            ShowSelectedPlan();
            Problem = changed.Message;
        }
        catch (Exception e)
        {
            // The switch rolled back (or never started); the previous profile is still active.
            Problem = e.Message;
        }
        finally
        {
            ProgressText = null;
            IsWorking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => Closed?.Invoke(this, false);

    private void Show(ProfileSwitchPreview preview)
    {
        _preview = preview;
        OffersChoice = preview.OffersNewer;
        CanUseSavedVersions = preview.Saved.Succeeded;
        SavedVersionsProblem = preview.Saved.Succeeded || !preview.OffersNewer ? null : string.Join("\n", preview.Saved.Problems);
        if (preview.Newest is { } newest)
        {
            NewerVersions = PlanText.Newer(preview.Saved, newest).Select(n => new NewerVersionViewModel(n, _changelog)).ToList();
            NewerOptionText = NewerVersions.Count == 1
                ? "Update to the newer version (1)"
                : $"Update to newer versions ({NewerVersions.Count})";
        }

        // The profile's versions by default; newer only when they're the only option.
        var useNewer = !preview.Saved.Succeeded && preview.OffersNewer;
        if (UseNewerVersions == useNewer)
        {
            ShowSelectedPlan();
        }
        else
        {
            UseNewerVersions = useNewer;
        }
    }

    private void ShowSelectedPlan()
    {
        OnPropertyChanged(nameof(IsReady));
        SwitchCommand.NotifyCanExecuteChanged();
        if (SelectedPlan is not { Succeeded: true } plan)
        {
            Changes = null;
            Summary = null;
            StatusText = "This profile can't be switched to:";
            Problem = string.Join("\n", (SelectedPlan ?? _preview?.Saved)?.Problems ?? []);
            return;
        }

        Problem = null;
        Changes = PlanText.Switch(plan);
        var parts = new List<string>();
        if (Changes.HasInstall)
        {
            parts.Add($"{Changes.Install.Count} to install");
        }
        if (Changes.HasChangeVersion)
        {
            parts.Add($"{Changes.ChangeVersion.Count} version{(Changes.ChangeVersion.Count == 1 ? "" : "s")} change");
        }
        if (Changes.HasRemove)
        {
            parts.Add($"{Changes.Remove.Count} to remove");
        }
        if (plan.DownloadBytes > 0)
        {
            parts.Add($"Download up to {ByteSize.Format(plan.DownloadBytes)}");
        }
        Summary = parts.Count == 0 ? null : string.Join(" · ", parts);
        StatusText = Changes.IsEmpty
            ? $"No mods change; only the active profile becomes \"{_profile.Name}\"."
            : "These changes will be made:";
    }
}

/// <summary>One newer version in the Switch profile dialog's update option; its changelog loads on first expand.</summary>
public partial class NewerVersionViewModel(NewerVersion version, Func<string, string, string, Task<string>> changelog) : ViewModelBase
{
    public string Text => version.Text;

    [ObservableProperty]
    public partial bool ShowChangelog { get; set; }

    [ObservableProperty]
    public partial string? Changelog { get; private set; }

    /// <summary>Completes when the most recently requested changelog has loaded; for tests.</summary>
    internal Task PendingChangelog { get; private set; } = Task.CompletedTask;

    partial void OnShowChangelogChanged(bool value)
    {
        if (value && Changelog is null)
        {
            PendingChangelog = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        Changelog = "Loading changelog…";
        try
        {
            Changelog = await changelog(version.Reference, version.Saved, version.Newer);
        }
        catch (Exception e)
        {
            Changelog = $"Couldn't load the changelog: {e.Message}";
        }
    }
}
