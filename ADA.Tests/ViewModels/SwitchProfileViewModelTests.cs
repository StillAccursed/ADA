using System.Security.Cryptography;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Profiles;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Ficsit;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

/// <summary>The Switch profile dialog: a preview first, the profile's versions by default, newer ones on request.</summary>
public sealed class SwitchProfileViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;
    private readonly ProfileStore _store;
    private readonly FakeHttpHandler _http = new();
    private readonly Dictionary<string, List<CatalogVersion>> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeVersionSource _versions;
    private readonly ModInstallService _service;
    private readonly GameProfiles _profiles;
    private readonly ModResolutionService _resolution;
    private readonly GameInstallViewModel _card;
    private bool _running;

    public SwitchProfileViewModelTests()
    {
        _game = new(_dir.Combine("Game"));
        _store = new(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        var install = new GameInstall(_game.Root, GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "Test", new(513885, "1.2"));
        var settings = new SettingsStore(_dir.Combine("settings.json"));
        settings.Save(new(DownloadRetries: 0));
        _versions = new FakeVersionSource((reference, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(reference) ?? []));
        _service = new(settings, new(new HttpClient(_http), _dir.Combine("Archives")), _versions, (_, _) => Task.FromResult(_running));
        _profiles = new(_store, _service);
        _resolution = new(_versions, _service);
        _card = new(install, _service, _resolution, _profiles, NullLogger<GameInstallViewModel>.Instance) { ProfileName = "Current" };
        _store.SetActive(_game.Root, "Current");
        _store.Save(new("Current", []));
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ReApplyingTheActiveProfileInstallsItsSavedVersionsWithoutOfferingNewer()
    {
        Publish("A", "1.0.0", "First release.");
        Publish("A", "1.1.0", "Fixed a crash.");
        var current = new ModProfile("Current", [new("A", "1.0.0")]);
        _store.Save(current);
        var dialog = Dialog(current);
        await dialog.PreviewAsync();

        Assert.True(dialog.IsReapply);
        Assert.Equal("Update", dialog.ApplyText);
        Assert.StartsWith("Bring ", dialog.Title);
        Assert.False(dialog.OffersChoice);
        Assert.Null(dialog.KeepsNote);
        Assert.Equal(["A 1.0.0"], dialog.Changes!.Install);

        await dialog.SwitchCommand.ExecuteAsync(null);

        Assert.Equal("1.0.0", Installer().Installed["A"].Version);
        Assert.Equal("Current", _store.ActiveName(_game.Root));
    }

    [Fact]
    public async Task DefaultsToTheProfilesVersionsAndSwitchesToNewerOnesOnRequest()
    {
        Publish("A", "1.0.0", "First release.");
        Publish("A", "1.1.0", "Fixed a crash.");
        var dialog = Dialog(new("Target", [new("A", "1.0.0")]));
        await dialog.PreviewAsync();

        Assert.True(dialog.OffersChoice);
        Assert.True(dialog.UseSavedVersions);
        Assert.Equal(["A 1.0.0"], dialog.Changes!.Install);
        Assert.Equal("Update to the newer version (1)", dialog.NewerOptionText);
        var newer = Assert.Single(dialog.NewerVersions);
        Assert.Equal("A 1.0.0 → 1.1.0", newer.Text);

        newer.ShowChangelog = true;
        await newer.PendingChangelog;
        Assert.Equal("1.1.0\nFixed a crash.", newer.Changelog);

        dialog.UseNewerVersions = true;
        Assert.Equal(["A 1.1.0"], dialog.Changes!.Install);

        bool? switched = null;
        dialog.Closed += (_, result) => switched = result;
        await dialog.SwitchCommand.ExecuteAsync(null);

        Assert.True(switched);
        Assert.Equal("1.1.0", Installer().Installed["A"].Version);
        Assert.Equal("1.1.0", Assert.Single(_store.Get("Target")!.Mods).Version);
        Assert.Equal("Target", _card.ProfileName);
    }

    [Fact]
    public async Task NoChoiceWhenTheSavedVersionsAreTheNewest()
    {
        Publish("A", "1.0.0");
        var dialog = Dialog(new("Target", [new("A", "1.0.0")]));

        await dialog.PreviewAsync();

        Assert.False(dialog.OffersChoice);
        Assert.True(dialog.IsReady);
        Assert.Equal("1 to install", dialog.Summary!.Split(" · ")[0]);
    }

    [Fact]
    public async Task AGoneSavedVersionDisablesThatOptionAndSelectsNewer()
    {
        Publish("A", "3.0.0");
        var dialog = Dialog(new("Target", [new("A", "2.0.0")]));

        await dialog.PreviewAsync();

        Assert.False(dialog.CanUseSavedVersions);
        Assert.True(dialog.UseNewerVersions);
        Assert.NotNull(dialog.SavedVersionsProblem);
        Assert.True(dialog.IsReady);
    }

    [Fact]
    public async Task ProblemsHideSwitchAndTheGameRunningDisablesIt()
    {
        var gone = Dialog(new("Target", [new("Missing", "1.0.0")]));
        await gone.PreviewAsync();
        Assert.False(gone.IsReady);
        Assert.NotNull(gone.Problem);

        Publish("A", "1.0.0");
        var dialog = Dialog(new("Target", [new("A", "1.0.0")]));
        await dialog.PreviewAsync();
        Assert.True(dialog.SwitchCommand.CanExecute(null));
        _running = true;
        dialog.RefreshCanSwitch();
        Assert.False(dialog.SwitchCommand.CanExecute(null));
    }

    [Fact]
    public async Task AChangedInstallationShowsTheNewChangesInsteadOfSwitching()
    {
        Publish("A", "1.0.0");
        var dialog = Dialog(new("Target", [new("A", "1.0.0")]));
        await dialog.PreviewAsync();
        await _service.InstallAsync(_card.Install, [Publish("B", "1.0.0")]);

        await dialog.SwitchCommand.ExecuteAsync(null);

        Assert.Equal("Current", _store.ActiveName(_game.Root));
        Assert.Contains("Something changed", dialog.Problem);
        Assert.Equal(["B"], dialog.Changes!.Remove);
        Assert.True(dialog.IsReady);
    }

    [Fact]
    public async Task ChoosingAProfileOnTheDashboardOpensThePreviewAndCancelRestoresTheSelection()
    {
        Publish("A", "1.0.0");
        _store.Save(new("Target", [new("A", "1.0.0")]));
        var store = new ModCatalogStore(_dir.Combine("catalog.db"));
        var catalog = new CatalogStatusViewModel(new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), store),
            NullLogger<CatalogStatusViewModel>.Instance);
        var dashboard = DashboardFactory.Create(_dir, new OneGame(_card.Install), _ => _card, catalog, store,
            installs: _service, profiles: _profiles, resolution: _resolution, changelogs: new ModChangelogs(_versions));
        dashboard.SelectedInstall = _card;
        await dashboard.RefreshCardsAsync();

        dashboard.SelectedProfile = dashboard.Profiles.Single(p => p.Name == "Target");
        await dashboard.PendingProfileSwitch;

        var dialog = Assert.IsType<SwitchProfileViewModel>(dashboard.SwitchPrompt);
        Assert.True(dialog.IsReady);
        Assert.Empty(Installer().Installed);
        Assert.Equal("Current", _store.ActiveName(_game.Root));

        dialog.CancelCommand.Execute(null);
        await dashboard.PendingProfileSwitch;

        Assert.Null(dashboard.SwitchPrompt);
        Assert.Equal("Current", dashboard.SelectedProfile?.Name);
        Assert.False(_card.IsSwitchingProfile);
    }

    private SwitchProfileViewModel Dialog(ModProfile profile) =>
        new(_card, profile, "Current", _profiles, _resolution, new ModChangelogs(_versions).ForRangeAsync, () => !_running);

    private ModInstaller Installer() => new(_game.Root, () => _running);

    private CatalogVersion Publish(string reference, string version, string? changelog = null)
    {
        var path = new SmodBuilder(reference, version).Typical().WriteTo(_dir.Combine("Published", reference, version));
        var data = File.ReadAllBytes(path);
        var link = $"/{reference}/{version}";
        var item = CatalogFixtures.Version(reference, version) with
        {
            Targets = [new(TargetPlatform.Windows, link, data.Length, Convert.ToHexStringLower(SHA256.HashData(data)))],
            Changelog = changelog,
        };
        if (!_catalog.TryGetValue(reference, out var versions))
        {
            versions = [];
            _catalog[reference] = versions;
        }
        versions.Add(item);
        _http.RespondBytes(data, link);
        return item;
    }

    private sealed class OneGame(GameInstall install) : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => [install];
    }
}
