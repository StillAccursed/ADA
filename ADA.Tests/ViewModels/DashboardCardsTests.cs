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

    [Fact]
    public async Task ImportRefreshesTheLibraryWithoutSwitchingOrChangingInstalledFiles()
    {
        var vm = Dashboard();
        var game = Card("First");
        _profileStore.Save(new("Active", []));
        _profileStore.SetActive(game.Install.GamePath, "Active");
        vm.SelectedInstall = game;
        await vm.RefreshCardsAsync();
        var sentinel = Path.Combine(game.Install.ModsPath, "unmanaged.txt");
        Directory.CreateDirectory(game.Install.ModsPath);
        File.WriteAllText(sentinel, "keep");
        var path = _dir.Combine("incoming.olp");
        OlpFile.Write(path, new("Imported", [new("SML", "1.0.0")]));

        Assert.True(vm.BeginProfileTransfer());
        try
        {
            Assert.False(vm.CanImportProfile);
            Assert.False(vm.CanExportProfile);
            Assert.False(vm.BeginProfileTransfer());
            await vm.ImportProfileAsync(path);
        }
        finally
        {
            vm.EndProfileTransfer();
        }

        Assert.Equal(["Active", "Imported"], vm.Profiles.Select(p => p.Name));
        Assert.Equal("Active", vm.SelectedProfile!.Name);
        Assert.Equal("Active", await _profiles.ActiveNameAsync(game.Install));
        Assert.Null(vm.SwitchPrompt);
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.True(vm.CanImportProfile);
        Assert.True(vm.CanExportProfile);
        Assert.Contains("Imported", vm.ProfileNotice);
    }

    [Fact]
    public async Task CancellingAPickerReleasesControlsWithoutChangingTheProfileOrNotice()
    {
        var vm = Dashboard();
        Assert.True(vm.CanImportProfile);
        Assert.False(vm.CanExportProfile);
        Assert.True(vm.BeginProfileTransfer());
        vm.EndProfileTransfer();

        await vm.RefreshCardsAsync();

        Assert.True(vm.CanImportProfile);
        Assert.False(vm.CanExportProfile);
        Assert.Null(vm.ProfileNotice);
        Assert.Empty(_profileStore.List());
    }

    [Fact]
    public async Task CancellingDeletionKeepsTheSavedProfileAndActiveSelection()
    {
        var vm = Dashboard();
        var game = Card("First");
        _profileStore.Save(new("Keep", []));
        _profileStore.SetActive(game.Install.GamePath, "Keep");
        vm.SelectedInstall = game;
        await vm.RefreshCardsAsync();

        var deletion = vm.DeleteProfileCommand.ExecuteAsync(null);
        var question = Assert.IsType<ConfirmViewModel>(vm.DeleteProfileQuestion);
        Assert.True(question.IsDanger);
        Assert.Contains("Installed mods will stay", question.Message);
        Assert.False(vm.CanChooseProfile);
        Assert.False(vm.CanImportProfile);
        question.Cancel();
        await deletion;

        Assert.NotNull(_profileStore.Get("Keep"));
        Assert.Equal("Keep", await _profiles.ActiveNameAsync(game.Install));
        Assert.Equal("Keep", vm.SelectedProfile!.Name);
        Assert.Null(vm.DeleteProfileQuestion);
        Assert.Null(vm.ProfileNotice);
        Assert.True(vm.CanChooseProfile);
    }

    [Fact]
    public async Task ConfirmedDeletionClearsSharedSelectionsWithoutDeletingInstalledFiles()
    {
        var vm = Dashboard();
        var game = Card("First");
        var other = Card("Second");
        _profileStore.Save(new("Shared", []));
        _profileStore.SetActive(game.Install.GamePath, "Shared");
        _profileStore.SetActive(other.Install.GamePath, "Shared");
        game.ProfileName = "Shared";
        other.ProfileName = "Shared";
        vm.Installs.Add(game);
        vm.Installs.Add(other);
        vm.SelectedInstall = game;
        await vm.RefreshCardsAsync();
        Directory.CreateDirectory(game.Install.ModsPath);
        var sentinel = Path.Combine(game.Install.ModsPath, "unmanaged.txt");
        File.WriteAllText(sentinel, "keep");

        var deletion = vm.DeleteProfileCommand.ExecuteAsync(null);
        vm.DeleteProfileQuestion!.ConfirmCommand.Execute(null);
        await deletion;

        Assert.Empty(_profileStore.List());
        Assert.Empty(vm.Profiles);
        Assert.Null(vm.SelectedProfile);
        Assert.Null(game.ProfileName);
        Assert.Null(other.ProfileName);
        Assert.Null(await _profiles.ActiveNameAsync(game.Install));
        Assert.Null(await _profiles.ActiveNameAsync(other.Install));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.False(vm.DeleteProfileCommand.CanExecute(null));
        Assert.Null(vm.ProfilePrompt);
        Assert.Contains("Deleted", vm.ProfileNotice);
    }

    [Fact]
    public async Task DeletionRechecksQueuedOperationsAfterConfirmation()
    {
        var vm = Dashboard();
        var game = Card("First");
        _profileStore.Save(new("Keep", []));
        _profileStore.SetActive(game.Install.GamePath, "Keep");
        vm.SelectedInstall = game;
        await vm.RefreshCardsAsync();

        var deletion = vm.DeleteProfileCommand.ExecuteAsync(null);
        game.HasModOperations = true;
        vm.DeleteProfileQuestion!.ConfirmCommand.Execute(null);
        await deletion;

        Assert.NotNull(_profileStore.Get("Keep"));
        Assert.Equal("Keep", await _profiles.ActiveNameAsync(game.Install));
        Assert.Contains("state changed", vm.ProfileNotice);
        Assert.False(vm.DeleteProfileCommand.CanExecute(null));
    }

    private DashboardViewModel Dashboard()
    {
        var catalog = new CatalogStatusViewModel(new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), _store),
            NullLogger<CatalogStatusViewModel>.Instance);
        return DashboardFactory.Create(_dir, new EmptyDetector(),
            game => new(game, _service, _resolution, _profiles, NullLogger<GameInstallViewModel>.Instance),
            catalog, _store, _settings, _service, _profiles, _resolution,
            profileTransfer: new ProfileTransfer(_profileStore, new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([]))));
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
