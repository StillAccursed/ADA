using System.Collections.ObjectModel;
using System.ComponentModel;
using ADA.Core.Catalog;
using ADA.Core.Profiles;
using ADA.Core.Settings;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Servers;
using ADA.Infrastructure.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>One cached release; no version-history request is needed to render it.</summary>
public sealed class DashboardModViewModel(CatalogMod mod)
{
    public string Name => mod.Name;

    public string Description => mod.ShortDescription;

    public string Version => mod.Latest?.FirstOrDefault(v => v.Stability == VersionStability.Release)?.Version
        ?? "Version unavailable";

    public string Released => mod.LastVersionDate?.ToLocalTime().ToString("g") ?? "Release date unavailable";
}

public partial class DashboardViewModel
{
    private readonly ModCatalogStore _store;
    private readonly SettingsStore _settings;
    private readonly GameProfiles _profiles;
    private readonly ModResolutionService _resolution;
    private readonly DashboardStorageReader _storage;
    private readonly ModChangelogs _changelogs;

    // Installations whose create-profile prompt was dismissed this session; "New profile" still opens it.
    private readonly HashSet<string> _promptDismissed = new(StringComparer.Ordinal);

    // The game, shared profile and differing configs the Multiplayer row last found; null when none differ.
    private (GameInstallViewModel Game, string Profile, IReadOnlyList<ConfigDifference> Differences)? _configSource;
    private SynchronizationContext? _uiContext;
    private CancellationTokenSource? _cardRefresh;

    // The newest card refresh. One that is superseded waits for it, so awaiting any refresh means the cards are current.
    private Task _newestCardRefresh = Task.CompletedTask;
    private bool _loadingProfiles;
    private int _cardRevision;
    private int _requestedRecentCount;

    public ObservableCollection<DashboardModViewModel> RecentMods { get; } = [];

    public ObservableCollection<ModProfile> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedInstall))]
    public partial GameInstallViewModel? SelectedInstall { get; set; }

    public bool HasSelectedInstall => SelectedInstall is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileModsText))]
    public partial ModProfile? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial DashboardDiskStatusViewModel Disk { get; private set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notifications))]
    public partial string? ScanError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notifications))]
    public partial string? CardError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notifications))]
    public partial string? ProfileNotice { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notifications))]
    internal partial string? OperationNotice { get; set; }

    [ObservableProperty]
    public partial string RecentModsState { get; private set; } = "Loading cached releases…";

    /// <summary>"Not installed" when no local copy was detected; otherwise whether a local game process is running.</summary>
    public string GameStateText => Installs.All(i => i.Install.IsRemote) ? "Not installed" : IsGameRunning ? "Running" : "Not running";

    /// <summary>For a server, the Disk card counts only what ADA installed, from its records.</summary>
    public string DiskModsLabel => SelectedInstall?.Install.IsRemote == true ? "ADA-installed mods" : "Mods folder size";

    public string DiskVolumeLabel => SelectedInstall?.Install.IsRemote == true ? "On the server" : "Installation drive";

    /// <summary>
    /// How the selected install and the connected server compare, for mods players need on both
    /// sides: in sync, which side is behind, or their two profiles. Null when there's no server or
    /// it has no profile yet.
    /// </summary>
    [ObservableProperty]
    public partial string? SyncText { get; private set; }

    /// <summary>The install that's behind its shared profile, which Update re-applies it to; null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncActionText), nameof(IsOutOfSync))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSyncTargetCommand))]
    public partial GameInstallViewModel? SyncTarget { get; private set; }

    public bool IsOutOfSync => SyncTarget is not null;

    /// <summary>"3 mod configs differ from your game"; null when they match or there's nothing to compare.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushConfigsCommand))]
    public partial string? ConfigsText { get; private set; }

    /// <summary>The open Push configs dialog, or null; the main window shows it and it clears itself when closed.</summary>
    [ObservableProperty]
    public partial PushConfigsViewModel? ConfigsPrompt { get; private set; }

    /// <summary>"Also apply to Home?" after a change in a game left the server behind; the main window shows it as a dialog.</summary>
    [ObservableProperty]
    public partial ConfirmViewModel? SyncQuestion { get; private set; }

    public string SyncActionText => SyncTarget?.Install.IsRemote == true ? $"Update {SyncTarget.Install.Location}" : "Update";

    /// <summary>The selected server's state for the Status card; null when a local install is selected.</summary>
    public string? ServerStateText => SelectedInstall is { Install.IsRemote: true } server
        ? server.IsUnavailable ? "Unavailable" : server.IsGameRunning ? "Running" : "Not running"
        : null;

    public string ProfilesText => $"{Profiles.Count} profile{(Profiles.Count == 1 ? "" : "s")}";

    public string ProfileModsText => SelectedProfile is { } profile
        ? $"{profile.Mods.Count} mod{(profile.Mods.Count == 1 ? "" : "s")} in profile"
        : SelectedInstall is null ? "No installation selected" : "This installation has no profile yet.";

    /// <summary>
    /// The open create-profile prompt, or null. Set when the selected installation has no profile
    /// (always the case on first run) and by <see cref="NewProfileCommand"/>; the main window shows
    /// it as a dialog and it clears itself when closed.
    /// </summary>
    [ObservableProperty]
    public partial CreateProfileViewModel? ProfilePrompt { get; private set; }

    /// <summary>
    /// The open Switch profile dialog, or null. Set when another profile is chosen in the Profiles
    /// card; the main window shows it as a dialog and it clears itself when closed.
    /// </summary>
    [ObservableProperty]
    public partial SwitchProfileViewModel? SwitchPrompt { get; private set; }

    public string LastCatalogUpdateText => Catalog.LastChecked?.ToLocalTime().ToString("g") ?? "Not checked yet";

    public bool CanChooseProfile => CanChooseProfileFor(SelectedInstall);

    /// <summary>Whether this install's profile can change now: nothing running on it, and it's reachable.</summary>
    private bool CanChooseProfileFor(GameInstallViewModel? game) =>
        game is { IsBusy: false, IsSwitchingProfile: false, HasModOperations: false, IsGameRunning: false, IsUnavailable: false }
        && !_profiles.IsChanging(game.Install);

    /// <summary>Current actionable conditions, deduplicated without inventing notification history.</summary>
    public string Notifications
    {
        get
        {
            var lines = new List<string?> { ServerProblem, ScanError, Catalog?.ErrorText, CardError, ProfileNotice, OperationNotice };
            foreach (var game in Installs)
            {
                var prefix = game.Install.DisplayName + ": ";
                if (!string.IsNullOrWhiteSpace(game.Notice))
                {
                    lines.Add(prefix + game.Notice);
                }
                if (game.HasRepairs)
                {
                    lines.Add(prefix + "Some mods need repair.");
                }
                if (game.HasFoundMods)
                {
                    lines.Add(prefix + "Found mods need your attention.");
                }
                if (game.HasFix || game.HasFixProblem)
                {
                    lines.Add(prefix + (game.FixProblem ?? "Some mods are missing dependencies."));
                }
                if (game.HasUpdates)
                {
                    lines.Add(prefix + game.UpdatesTitle);
                }
            }
            var notices = lines.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            return notices.Count == 0 ? "No action needed." : string.Join("\n\n", notices);
        }
    }

    /// <summary>
    /// Completes once the most recently chosen profile's switch preview is worked out (or refused), and
    /// again once its dialog has closed and the cards are refreshed.
    /// </summary>
    internal Task PendingProfileSwitch { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Reads cached releases, profiles and storage. A newer refresh cancels this one's snapshot; this
    /// one then completes when the newer one does, so awaiting any refresh means the cards are current.
    /// </summary>
    public async Task RefreshCardsAsync()
    {
        _uiContext ??= SynchronizationContext.Current;
        var recentCount = _settings.Current.DashboardLatestModsCount;
        _requestedRecentCount = recentCount;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Published before the older refresh is cancelled, so it always finds this one to wait for.
        Volatile.Write(ref _newestCardRefresh, done.Task);
        _cardRefresh?.Cancel();
        var source = new CancellationTokenSource();
        _cardRefresh = source;
        var ct = source.Token;
        var revision = ++_cardRevision;
        var game = SelectedInstall;
        Task? supersededBy = null;
        try
        {
            // Coalesce bursty check/profile/operation notifications into one filesystem scan.
            await Task.Delay(100, ct);
            var recentTask = Task.Run(() => _store.GetRecentlyUpdated(recentCount), ct);
            var profileTask = _profiles.ListAsync();
            var diskTask = game?.Install.IsRemote == true ? ReadServerStorageAsync(game, ct) : _storage.ReadAsync(game?.Install.ModsPath, ct);
            await Task.WhenAll(recentTask, profileTask, diskTask);
            ct.ThrowIfCancellationRequested();
            if (revision != _cardRevision)
            {
                supersededBy = Volatile.Read(ref _newestCardRefresh);
                return;
            }
            var recent = await recentTask;
            RecentMods.Clear();
            foreach (var mod in recent)
            {
                RecentMods.Add(new(mod));
            }
            RecentModsState = recent.Count == 0 ? "No cached releases yet." : "";
            Disk = new(await diskTask);
            if (game?.IsSwitchingProfile != true)
            {
                _loadingProfiles = true;
                string? activeName;
                try
                {
                    activeName = game is null ? null : await _profiles.ActiveNameAsync(game.Install);
                    ct.ThrowIfCancellationRequested();
                    Profiles.Clear();
                    foreach (var profile in await profileTask)
                    {
                        Profiles.Add(profile);
                    }
                    SelectedProfile = activeName is null
                        ? null : Profiles.FirstOrDefault(p => p.Name.Equals(activeName, StringComparison.OrdinalIgnoreCase));
                    if (game is not null)
                    {
                        game.ProfileName = activeName;
                    }
                    OnPropertyChanged(nameof(ProfilesText));
                    OnPropertyChanged(nameof(ProfileModsText));
                    OnPropertyChanged(nameof(CanChooseProfile));
                }
                finally
                {
                    _loadingProfiles = false;
                }
                if (game is { IsBusy: false } && activeName is null && !_promptDismissed.Contains(game.Install.GamePath))
                {
                    OpenProfilePrompt(game);
                }
            }
            var sync = await ReadSyncAsync(game, ct);
            var configs = await ReadConfigsAsync(sync, ct);
            ct.ThrowIfCancellationRequested();
            if (revision == _cardRevision)
            {
                SyncText = sync.Text;
                SyncTarget = sync.Target;
                _configSource = configs is { Count: > 0 } ? (sync.ConfigSource!, sync.Profile!, configs) : null;
                var from = ReferenceEquals(sync.ConfigSource, game) ? "this game" : sync.ConfigSource?.Install.DisplayName;
                ConfigsText = configs is { Count: > 0 }
                    ? $"{configs.Count} mod config{(configs.Count == 1 ? "" : "s")} on the server differ{(configs.Count == 1 ? "s" : "")} from {from}."
                    : null;
            }
            CardError = null;
        }
        catch (OperationCanceledException)
        {
            // The previous snapshot belongs to an old selection or refresh.
            supersededBy = Volatile.Read(ref _newestCardRefresh);
        }
        catch (Exception e)
        {
            if (revision == _cardRevision)
            {
                CardError = "Couldn't refresh dashboard: " + e.Message;
                if (RecentMods.Count == 0)
                {
                    RecentModsState = "Cached releases are unavailable.";
                }
            }
        }
        finally
        {
            source.Dispose();
            if (ReferenceEquals(_cardRefresh, source))
            {
                _cardRefresh = null;
            }
            // In the finally block so the early return for a superseded revision waits too. Done is
            // set only after the wait, so anyone waiting on this refresh waits down the whole chain.
            if (supersededBy is not null && !ReferenceEquals(supersededBy, done.Task))
            {
                await supersededBy;
            }
            done.SetResult();
        }
    }

    /// <summary>
    /// The Disk card's figures for a server: the size of the mods ADA installed there, from its
    /// records, and the server's free space. Archives are local and measured as usual.
    /// </summary>
    private async Task<DashboardStorageSnapshot> ReadServerStorageAsync(GameInstallViewModel game, CancellationToken ct)
    {
        var local = await _storage.ReadAsync(null, ct);
        FolderMeasurement mods;
        try
        {
            var bytes = await Task.Run(() => _installs.InstallerFor(game.Install).Installed.Values.Sum(m => m.Files.Sum(f => f.Size)), ct);
            mods = new FolderMeasurement(bytes, true);
        }
        catch (IOException e)
        {
            mods = new FolderMeasurement(0, false, e.Message);
        }
        var free = _servers is null ? null : await _servers.AvailableFreeSpaceAsync(game.Install, ct);
        return local with { Mods = mods, GameVolume = new VolumeMeasurement(game.Install.Location, free, null) };
    }

    /// <summary>Called from application startup on the UI thread; services are constructed on a worker.</summary>
    internal void AttachUiContext() => _uiContext = SynchronizationContext.Current;

    /// <summary>Retains failures from this operation batch until the user starts a new batch.</summary>
    internal void ReportOperationProblem(string problem)
    {
        OperationNotice = string.IsNullOrEmpty(OperationNotice) ? problem : OperationNotice + "\n" + problem;
    }

    partial void OnSelectedInstallChanged(GameInstallViewModel? value)
    {
        ProfileNotice = null;
        OnPropertyChanged(nameof(CanChooseProfile));
        OnPropertyChanged(nameof(ServerStateText));
        OnPropertyChanged(nameof(DiskModsLabel));
        OnPropertyChanged(nameof(DiskVolumeLabel));
        Disk = new();
        _ = RefreshCardsAsync();
    }

    partial void OnSelectedProfileChanged(ModProfile? value)
    {
        if (!_loadingProfiles && value is not null && SelectedInstall is { } game
            && !string.Equals(game.ProfileName, value.Name, StringComparison.OrdinalIgnoreCase))
        {
            PendingProfileSwitch = OpenSwitchPromptAsync(game, value);
        }
    }

    /// <summary>Opens the create-profile prompt for the selected installation, even if it was dismissed.</summary>
    [RelayCommand]
    private void NewProfile()
    {
        if (SelectedInstall is not { } game || !CanChooseProfile)
        {
            ProfileNotice = "Wait for current operations to finish and close Satisfactory before creating a profile.";
            return;
        }
        OpenProfilePrompt(game);
    }

    /// <summary>Shows one prompt at a time; dismissing it stops the automatic prompt for that installation.</summary>
    private void OpenProfilePrompt(GameInstallViewModel game)
    {
        if (ProfilePrompt is not null)
        {
            return;
        }
        var prompt = new CreateProfileViewModel(game, _profiles, Profiles.Select(p => p.Name).ToList());
        prompt.Closed += (_, created) =>
        {
            ProfilePrompt = null;
            if (created)
            {
                ProfileNotice = $"{game.ProfileName} is active for {game.Install.DisplayName}.";
            }
            else
            {
                _promptDismissed.Add(game.Install.GamePath);
            }
            _ = RefreshCardsAsync();
        };
        ProfilePrompt = prompt;
    }

    /// <summary>
    /// After mods changed in a local game: if the connected server shares its profile, can change now
    /// and is behind on mods players need, asks once whether to update it, and opens the update on yes.
    /// Does nothing while another such question is open. Never throws.
    /// </summary>
    internal async Task OfferServerUpdateAsync(GameInstallViewModel game)
    {
        if (_sync is null || _serverInstall is not { } server || game.Install.IsRemote || !CanChooseProfileFor(server)
            || SyncQuestion is not null)
        {
            return;
        }

        try
        {
            var shared = await _profiles.ActiveNameAsync(server.Install);
            if (shared is null || !string.Equals(shared, await _profiles.ActiveNameAsync(game.Install), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var state = await _sync.CompareAsync(server.Install);
            if (state.InSync || SyncQuestion is not null)
            {
                return;
            }

            var name = server.Install.Location;
            var question = new ConfirmViewModel($"Also apply to {name}?",
                $"Your game and {name} share \"{shared}\", and {name} now differs in {state.Count} mod{(state.Count == 1 ? "" : "s")} players need: {string.Join(", ", state.Missing.Concat(state.Extra))}.",
                $"Update {name}", "Not now");
            SyncQuestion = question;
            bool update;
            try
            {
                update = await question.Answer;
            }
            finally
            {
                SyncQuestion = null;
            }
            if (update)
            {
                await UpdateToProfileAsync(server);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogWarning(e, "Checking whether {Server} needs updating failed", server.Install.GamePath);
        }
    }

    /// <summary>Opens the Push configs dialog for the pair the Multiplayer row compared.</summary>
    [RelayCommand(CanExecute = nameof(HasConfigsToPush))]
    private async Task PushConfigsAsync()
    {
        if (_configPush is null || _serverInstall is not { } server || _configSource is not var (game, profile, differences)
            || ConfigsPrompt is not null)
        {
            return;
        }
        bool hasBackup;
        try
        {
            hasBackup = await _configPush.HasBackupAsync(server.Install);
        }
        catch (ServerUnavailableException)
        {
            hasBackup = false;
        }
        var prompt = new PushConfigsViewModel(_configPush, game, server, profile, differences, hasBackup);
        prompt.Closed += (_, changed) =>
        {
            ConfigsPrompt = null;
            if (changed)
            {
                _ = RefreshCardsAsync();
            }
        };
        ConfigsPrompt = prompt;
    }

    private bool HasConfigsToPush => ConfigsText is not null;

    /// <summary>
    /// Configs that differ between the game and server <paramref name="sync"/> paired; null when it paired
    /// none, the server isn't connected, or the comparison failed (the row then just doesn't mention configs).
    /// </summary>
    private async Task<IReadOnlyList<ConfigDifference>?> ReadConfigsAsync(SyncRead sync, CancellationToken ct)
    {
        if (_configPush is null || sync.ConfigSource is not { } game || _serverInstall is not { IsUnavailable: false } server)
        {
            return null;
        }
        try
        {
            return await _configPush.CompareAsync(game.Install, server.Install, ct);
        }
        catch (Exception e) when (e is IOException or ServerUnavailableException)
        {
            _log.LogWarning(e, "Couldn't compare mod configs with {Server}", server.Install.GamePath);
            return null;
        }
    }

    /// <param name="Text">The Multiplayer row's line; null hides it.</param>
    /// <param name="Target">The install that's behind its shared profile, which Update re-applies.</param>
    /// <param name="ConfigSource">The game whose configs are compared with the server's; null when there's no available pair.</param>
    /// <param name="Profile">The profile the pair shares.</param>
    private sealed record SyncRead(string? Text, GameInstallViewModel? Target = null, GameInstallViewModel? ConfigSource = null,
        string? Profile = null);

    /// <summary>Re-applies the shared profile to the install that's behind, through the Switch profile dialog.</summary>
    [RelayCommand(CanExecute = nameof(IsOutOfSync))]
    private Task UpdateSyncTargetAsync() => SyncTarget is { } target ? UpdateToProfileAsync(target) : Task.CompletedTask;

    /// <summary>Opens the Switch profile dialog for the install's own active profile, which re-applies it.</summary>
    private async Task UpdateToProfileAsync(GameInstallViewModel target)
    {
        var name = await _profiles.ActiveNameAsync(target.Install);
        if ((await _profiles.ListAsync()).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } profile)
        {
            await OpenSwitchPromptAsync(target, profile);
        }
    }

    /// <summary>
    /// Compares the selected install with the connected server through their shared profile. With the
    /// server selected, every local game sharing its profile is compared. The first side found behind
    /// is the target for Update; the server is checked first, since players can't join it until it
    /// matches. Also names the game whose configs are compared with the server's. Never throws: a
    /// failed comparison reads as "can't be checked".
    /// </summary>
    private async Task<SyncRead> ReadSyncAsync(GameInstallViewModel? game, CancellationToken ct)
    {
        if (_sync is null || _serverInstall is not { } server || game is null)
        {
            return new SyncRead(null);
        }

        var serverName = server.Install.Location;
        try
        {
            if (await _profiles.ActiveNameAsync(server.Install) is not { } shared)
            {
                return new SyncRead(null);
            }

            var selectedIsServer = ReferenceEquals(game, server);
            var sharing = new List<GameInstallViewModel>();
            foreach (var local in selectedIsServer ? Installs.Where(i => !i.Install.IsRemote).ToList() : [game])
            {
                var name = await _profiles.ActiveNameAsync(local.Install);
                if (string.Equals(name, shared, StringComparison.OrdinalIgnoreCase))
                {
                    sharing.Add(local);
                }
                else if (!selectedIsServer)
                {
                    return new SyncRead(name is null ? null : $"{serverName} uses \"{shared}\"; this game uses \"{name}\".");
                }
            }
            if (sharing.Count == 0)
            {
                return new SyncRead($"{serverName} uses \"{shared}\"; none of your games do.");
            }
            if (server.IsUnavailable)
            {
                return new SyncRead($"{serverName} can't be checked right now.");
            }

            // Configs are pushed from the selected game, or with the server selected, the first game sharing its profile.
            var source = sharing[0];
            var serverState = await _sync.CompareAsync(server.Install, ct);
            if (!serverState.InSync)
            {
                return new SyncRead(Behind(serverName, serverState), server, source, shared);
            }
            foreach (var local in sharing)
            {
                var state = await _sync.CompareAsync(local.Install, ct);
                if (!state.InSync)
                {
                    return new SyncRead(Behind(selectedIsServer ? local.Install.DisplayName : "This game", state), local, source, shared);
                }
            }
            return new SyncRead(selectedIsServer ? $"In sync with {string.Join(", ", sharing.Select(s => s.Install.DisplayName))}." : $"In sync with {serverName}.",
                null, source, shared);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new SyncRead($"{serverName} can't be checked right now: {e.Message}");
        }

        static string Behind(string who, SyncState state)
        {
            var parts = new List<string>();
            if (state.Missing.Count > 0)
            {
                parts.Add($"needs {string.Join(", ", state.Missing)}");
            }
            if (state.Extra.Count > 0)
            {
                parts.Add($"has {string.Join(", ", state.Extra)}, which \"{state.Profile}\" doesn't");
            }
            return $"{who} differs from \"{state.Profile}\" in {state.Count} mod{(state.Count == 1 ? "" : "s")} players need: {string.Join("; ", parts)}.";
        }
    }

    /// <summary>
    /// Opens the Switch profile dialog for <paramref name="profile"/>, which needn't be saved yet: a switch
    /// saves it when it succeeds, and Cancel leaves nothing behind. Used by a save's "Switch to this save's
    /// mods". The dialog shows over any page.
    /// </summary>
    /// <returns>False when the install can't switch now (game running, busy, unavailable, or a dialog already open).</returns>
    internal async Task<bool> OpenSwitchToAsync(GameInstallViewModel game, ModProfile profile)
    {
        if (!CanChooseProfileFor(game) || SwitchPrompt is not null)
        {
            return false;
        }
        await OpenSwitchPromptAsync(game, profile);
        return true;
    }

    /// <summary>
    /// Shows what switching to <paramref name="profile"/> would change, instead of switching at once.
    /// The installation is marked as switching while the dialog is open, so nothing else starts on it;
    /// closing restores the dropdown to the active profile unless the switch succeeded.
    /// </summary>
    private async Task OpenSwitchPromptAsync(GameInstallViewModel game, ModProfile profile)
    {
        if (!CanChooseProfileFor(game) || SwitchPrompt is not null)
        {
            ProfileNotice = "Wait for current operations to finish and close Satisfactory before switching profiles.";
            await RefreshCardsAsync();
            return;
        }
        var prompt = new SwitchProfileViewModel(game, profile, game.ProfileName, _profiles, _resolution, _changelogs.ForRangeAsync,
            () => !game.IsGameRunning && !game.IsUnavailable && !_profiles.IsChanging(game.Install));
        game.IsSwitchingProfile = true;
        ProfileNotice = null;
        prompt.Closed += (_, switched) => PendingProfileSwitch = CloseSwitchPromptAsync(game, profile, switched);
        SwitchPrompt = prompt;
        await prompt.PreviewAsync();
    }

    /// <summary>
    /// Releases the installation once the Switch profile dialog closes, reports a successful switch, and
    /// re-reads the cards, which also puts the dropdown back on the active profile after Cancel.
    /// </summary>
    private async Task CloseSwitchPromptAsync(GameInstallViewModel game, ModProfile profile, bool switched)
    {
        SwitchPrompt = null;
        game.IsSwitchingProfile = false;
        if (switched)
        {
            ProfileNotice = $"{profile.Name} is active for {game.Install.DisplayName}.";
        }
        await game.CheckAsync();
        await RefreshCardsAsync();
    }

    private void OnCardSourceChanged(object? sender, EventArgs e)
    {
        void RefreshFromSource()
        {
            if (ReferenceEquals(sender, _settings) && _settings.Current.DashboardLatestModsCount == _requestedRecentCount)
            {
                return;
            }
            _ = RefreshCardsAsync();
        }
        if (_uiContext is { } context && SynchronizationContext.Current != context)
        {
            context.Post(_ => RefreshFromSource(), null);
        }
        else
        {
            RefreshFromSource();
        }
    }

    private void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(LastCatalogUpdateText));
        OnPropertyChanged(nameof(Notifications));
        if (e.PropertyName == nameof(CatalogStatusViewModel.IsChecking) && !Catalog.IsChecking)
        {
            _ = RefreshCardsAsync();
        }
    }
}
