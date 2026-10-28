using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Settings;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.Ficsit;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

public sealed class DashboardCardsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ModCatalogStore _store;
    private readonly SettingsStore _settings;
    private readonly ProfileStore _profileStore;
    private readonly GameProfiles _profiles;
    private readonly ModInstallService _service;
    private readonly ModResolutionService _resolution;

    public DashboardCardsTests()
    {
        _store = new(_dir.Combine("catalog.db"));
        _settings = new(_dir.Combine("settings.json"));
        _profileStore = new(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        _service = new(_settings, new(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
            (_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([]), _ => Task.FromResult(false));
        _profiles = new(_profileStore, _service);
        _resolution = new((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([]), _service);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Refreshing_and_changing_the_limit_never_create_a_Default_profile()
    {
        _store.Upsert(Enumerable.Range(1, 8).Select(i => CatalogFixtures.Mod($"Mod{i}", lastVersion: DateTimeOffset.UtcNow.AddDays(-i))), DateTimeOffset.UtcNow);
        var vm = Dashboard();
        vm.SelectedInstall = Card("First");
        await vm.RefreshCardsAsync();
        Assert.Equal(5, vm.RecentMods.Count);
        Assert.Equal("Default", vm.SelectedProfile?.Name);
        Assert.Empty(_profileStore.List());

        _settings.Save(new AppSettings(DashboardLatestModsCount: 2));
        await vm.RefreshCardsAsync();
        Assert.Equal(["Mod1", "Mod2"], vm.RecentMods.Select(m => m.Name));
        Assert.Empty(_profileStore.List());
    }

    [Fact]
    public async Task A_new_selection_supersedes_an_in_flight_storage_and_profile_refresh()
    {
        var vm = Dashboard();
        var first = Card("First");
        var second = Card("Second");
        _profileStore.Save(new("Second profile", []));
        _profileStore.SetActive(second.Install.GamePath, "Second profile");
        vm.SelectedInstall = first;
        var pending = vm.RefreshCardsAsync();
        vm.SelectedInstall = second;
        await vm.RefreshCardsAsync();
        await pending;
        Assert.Same(second, vm.SelectedInstall);
        Assert.Equal("Second profile", vm.SelectedProfile?.Name);
        Assert.Equal("Second profile", second.ProfileName);
    }

    [Fact]
    public async Task Queued_mod_operations_disable_profile_selection_and_cannot_be_overtaken()
    {
        var vm = Dashboard();
        var card = Card("First");
        vm.SelectedInstall = card;
        await vm.RefreshCardsAsync();
        card.HasModOperations = true;
        Assert.False(vm.CanChooseProfile);
        vm.SelectedProfile = new("Blocked", []);
        await vm.PendingProfileSwitch;
        Assert.Equal("Default", await _profiles.ActiveNameAsync(card.Install));
        Assert.Equal("Default", vm.SelectedProfile?.Name);
    }

    private DashboardViewModel Dashboard()
    {
        var catalog = new CatalogStatusViewModel(new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), _store),
            NullLogger<CatalogStatusViewModel>.Instance);
        return new(new EmptyDetector(), catalog, game => new(game, _service, _resolution, _profiles, NullLogger<GameInstallViewModel>.Instance),
            NullLogger<DashboardViewModel>.Instance, store: _store, settings: _settings, profiles: _profiles,
            resolution: _resolution, storage: new(_dir.Combine("Archives")));
    }

    private GameInstallViewModel Card(string folder)
    {
        var game = new GameInstall(_dir.CreateGame(folder), GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "Test", new(513885, "1.2"));
        return new(game, _service, _resolution, _profiles, NullLogger<GameInstallViewModel>.Instance);
    }

    private sealed class EmptyDetector : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => [];
    }
}
