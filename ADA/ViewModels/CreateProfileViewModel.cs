using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADA.ViewModels;

/// <summary>
/// The "Create a profile" prompt. Satisfactory has no profiles of its own, so ADA needs one for
/// each installation before it can keep track of what to restore on a switch. Creating saves the
/// mods ADA manages in that installation as the new profile and makes it active.
/// </summary>
public partial class CreateProfileViewModel : ViewModelBase
{
    private readonly GameInstallViewModel _game;
    private readonly GameProfiles _profiles;

    /// <summary>Raised on the UI thread once the prompt should close: true after creating, false when dismissed.</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>No profiles exist yet; the prompt then explains what profiles are for.</summary>
    public bool IsFirstProfile { get; }

    public string Title => IsFirstProfile ? "Create your first profile" : "Create a profile";

    public string Explanation => (IsFirstProfile
            ? "Satisfactory doesn't support profiles, so ADA keeps one for each playthrough. Switching profiles "
              + "moves the current profile's mods out of the Mods folder and the chosen profile's mods in. "
            : "")
        + $"The mods ADA manages in {_game.Install.DisplayName} will be saved to this profile.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string? Problem { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(DismissCommand))]
    public partial bool IsCreating { get; set; }

    private bool CanCreate => !IsCreating && !string.IsNullOrWhiteSpace(Name);

    private bool CanDismiss => !IsCreating;

    /// <param name="existing">Saved profile names, used to suggest an unused "Profile N".</param>
    public CreateProfileViewModel(GameInstallViewModel game, GameProfiles profiles, IReadOnlyCollection<string> existing)
    {
        _game = game;
        _profiles = profiles;
        IsFirstProfile = existing.Count == 0;
        Name = SuggestName(existing);
    }

    /// <summary>Saves the profile; a name or busy problem keeps the prompt open and shows why.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        IsCreating = true;
        Problem = null;
        try
        {
            var profile = await _profiles.CreateAsync(_game.Install, Name);
            _game.ProfileName = profile.Name;
            Closed?.Invoke(this, true);
        }
        catch (Exception e) when (e is InvalidProfileException or InstallException or IOException or UnauthorizedAccessException)
        {
            Problem = e.Message;
        }
        finally
        {
            IsCreating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDismiss))]
    private void Dismiss() => Closed?.Invoke(this, false);

    private static string SuggestName(IReadOnlyCollection<string> existing)
    {
        for (var n = 1; ; n++)
        {
            var name = $"Profile {n}";
            if (!existing.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return name;
            }
        }
    }
}
