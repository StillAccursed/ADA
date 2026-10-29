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
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), (_, _) => Task.FromResult(false));
        _profiles = new(_profileStore, _service);
        _resolution = new(new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), _service);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task RefreshingAndChangingTheLimitNeverCreateAProfileByThemselves()
    {
        _store.Upsert(Enumerable.Range(1, 8).Select(i => CatalogFixtures.Mod($"Mod{i}", lastVersion: DateTimeOffset.UtcNow.AddDays(-i))), DateTimeOffset.UtcNow);
        var vm = Dashboard();
        vm.SelectedInstall = Card("First");
        await vm.RefreshCardsAsync();
        Assert.Equal(5, vm.RecentMods.Count);
        Assert.Null(vm.SelectedProfile);
        Assert.Empty(vm.Profiles);
        Assert.Empty(_profileStore.List());

        _settings.Save(new AppSettings(DashboardLatestModsCount: 2));
        await vm.RefreshCardsAsync();
        Assert.Equal(["Mod1", "Mod2"], vm.RecentMods.Select(m => m.Name));
        Assert.Empty(_profileStore.List());
    }

    [Fact]
    public async Task ANewSelectionSupersedesAnInFlightStorageAndProfileRefresh()
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
    public async Task QueuedModOperationsDisableProfileSelectionAndCannotBeOvertaken()
    {
        var vm = Dashboard();
        var card = Card("First");
        vm.SelectedInstall = card;
        await vm.RefreshCardsAsync();
        card.HasModOperations = true;
        Assert.False(vm.CanChooseProfile);
        vm.SelectedProfile = new("Blocked", []);
        await vm.PendingProfileSwitch;
        Assert.Null(await _profiles.ActiveNameAsync(card.Install));
        Assert.Null(vm.SelectedProfile);
    }

    [Fact]
    public async Task FirstRunPromptsForAProfileAndCreatingItMakesItActive()
    {
        var vm = Dashboard();
        var card = Card("First");
        vm.SelectedInstall = card;
        await vm.RefreshCardsAsync();

        var prompt = Assert.IsType<CreateProfileViewModel>(vm.ProfilePrompt);
        Assert.True(prompt.IsFirstProfile);
        Assert.Equal("Profile 1", prompt.Name);
        await prompt.CreateCommand.ExecuteAsync(null);
        await vm.RefreshCardsAsync();

        Assert.Null(vm.ProfilePrompt);
        Assert.Equal("Profile 1", await _profiles.ActiveNameAsync(card.Install));
        Assert.Equal("Profile 1", vm.SelectedProfile?.Name);
        Assert.Equal("Profile 1", card.ProfileName);
    }

    [Fact]
    public async Task AwaitingASupersededRefreshWaitsForTheNewerOne()
    {
        var vm = Dashboard();
        var card = Card("First");
        vm.SelectedInstall = card;
        var first = vm.RefreshCardsAsync();
        _profileStore.Save(new("Later", []));
        _profileStore.SetActive(card.Install.GamePath, "Later");
        _ = vm.RefreshCardsAsync();

        await first;

        Assert.Equal("Later", vm.SelectedProfile?.Name);
    }

    [Fact]
    public async Task ADismissedPromptStaysClosedUntilNewProfileIsChosen()
    {
        _profileStore.Save(new("Profile 1", []));
        var vm = Dashboard();
        vm.SelectedInstall = Card("First");
        await vm.RefreshCardsAsync();
        Assert.False(vm.ProfilePrompt!.IsFirstProfile);
        Assert.Equal("Profile 2", vm.ProfilePrompt.Name);

        vm.ProfilePrompt.DismissCommand.Execute(null);
        await vm.RefreshCardsAsync();
        Assert.Null(vm.ProfilePrompt);

        vm.NewProfileCommand.Execute(null);
        Assert.NotNull(vm.ProfilePrompt);
    }

    private DashboardViewModel Dashboard()
    {
        var catalog = new CatalogStatusViewModel(new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), _store),
            NullLogger<CatalogStatusViewModel>.Instance);
        return DashboardFactory.Create(_dir, new EmptyDetector(),
            game => new(game, _service, _resolution, _profiles, NullLogger<GameInstallViewModel>.Instance),
            catalog, _store, _settings, _service, _profiles, _resolution);
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
