using System.Security.Cryptography;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Settings;
using ADA.Imaging;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.Ficsit;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Platform.Detection;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

/// <summary>The Mods page from search to a one-click install and uninstall, against a fake game folder.</summary>
public sealed class ModsViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;
    private readonly FakeHttpHandler _http = new();
    private readonly ModCatalogStore _store;
    private readonly Dictionary<string, List<CatalogVersion>> _versions = new(StringComparer.OrdinalIgnoreCase);
    private bool _gameRunning;

    public ModsViewModelTests()
    {
        _game = new GameFolder(_dir.Combine("Satisfactory"));
        _store = new ModCatalogStore(_dir.Combine("catalog.db"));
    }

    public void Dispose() => _dir.Dispose();

    private void Publish(string reference, string version, string description = "", params VersionDependency[] deps)
    {
        var path = new SmodBuilder(reference, version).Typical().WriteTo(_dir.Combine("Published", reference, version));
        var bytes = File.ReadAllBytes(path);
        var link = $"/v1/version/{reference}-{version}/Windows/download";
        _http.RespondBytes(bytes, link);
        if (!_versions.TryGetValue(reference, out var list))
        {
            _versions[reference] = list = [];
        }

        list.Add(new CatalogVersion($"{reference}-{version}", reference, version, ">=513885", VersionStability.Release, false,
            DateTimeOffset.UnixEpoch,
            [new VersionTarget(TargetPlatform.Windows, link, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))], deps));
        _store.Upsert([CatalogFixtures.Mod(reference, shortDescription: description, fullDescription: description)], DateTimeOffset.UtcNow);
    }

    private SettingsStore Settings => new(_dir.Combine("Config", "settings.json"));

    private async Task<ModsViewModel> OpenPageAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<CatalogVersion>>>? resolveVersions = null,
        GameFolder? otherGame = null,
        Action<DashboardViewModel>? dashboardReady = null,
        SettingsStore? settingsStore = null)
    {
        GameInstall CreateInstall(string root) => new(root, GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "test",
            new GameBuild(513885, "1.2.4.1"));
        var install = CreateInstall(_game.Root);
        Task<IReadOnlyList<CatalogVersion>> GetVersions(string r, CancellationToken _) =>
            Task.FromResult<IReadOnlyList<CatalogVersion>>(_versions.GetValueOrDefault(r) ?? []);
        var installs = new ModInstallService(
            new SettingsStore(_dir.Combine("settings.json")),
            new ArchiveDownloader(new HttpClient(_http), _dir.Combine("Archives")),
            new FakeVersionSource(GetVersions),
            (_, _) => Task.FromResult(_gameRunning));
        var profiles = new GameProfiles(new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json")), installs);
        var catalog = new CatalogStatusViewModel(
            new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), _store),
            NullLogger<CatalogStatusViewModel>.Instance);
        var dashboard = DashboardFactory.Create(_dir,
            new KnownGames(otherGame is null ? [install] : [install, CreateInstall(otherGame.Root)]),
            game => new GameInstallViewModel(game, installs, new ModResolutionService(new FakeVersionSource(GetVersions), installs), profiles, NullLogger<GameInstallViewModel>.Instance),
            catalog, _store, installs: installs, profiles: profiles);
        dashboardReady?.Invoke(dashboard);
        var page = new ModsViewModel(_store, new FakeVersionSource(GetVersions), new ModResolutionService(new FakeVersionSource(resolveVersions ?? GetVersions), installs), installs, profiles, dashboard,
            new ModLogos(new HttpClient(new FakeHttpHandler())), new FavouritesStore(_dir.Combine("favourites.json")),
            NullLogger<ModsViewModel>.Instance, settingsStore ?? Settings);

        // Selecting the first detected game starts a refresh. Finish it before starting another,
        // so assertions cannot read the results while that initial search is replacing them.
        var initialSearch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModsViewModel.ResultsText))
            {
                initialSearch.TrySetResult();
            }
        }

        page.PropertyChanged += OnPageChanged;
        try
        {
            await dashboard.ScanCommand.ExecuteAsync(null);
            await initialSearch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            page.PropertyChanged -= OnPageChanged;
        }
        await page.RefreshAsync();
        return page;
    }

    private static ModItemViewModel Find(ModsViewModel page, string reference) =>
        page.Results.Single(r => r.Reference == reference);

    [Fact]
    public async Task SearchThenInstallInstallsDependenciesWithoutConfirmation()
    {
        Publish("SML", "3.12.0", "Mod loader");
        Publish("WeatherExtended", "1.1.0", "Rain and snow", new VersionDependency("SML", "^3.12.0", false));
        var page = await OpenPageAsync();

        page.SearchText = "rain";
        await page.PendingSearch;
        page.SelectedMod = Assert.Single(page.Results);

        await page.InstallCommand.ExecuteAsync(null);

        Assert.False(page.IsBusy);
        Assert.Null(page.ProgressText);
        Assert.Equal("Installed SML 3.12.0, WeatherExtended 1.1.0.", page.Notice);
        Assert.NotNull(_game.Read("Mods/SML"));
        Assert.Equal("Installed 1.1.0", page.SelectedMod!.InstalledText);

        page.SearchText = "";
        await page.PendingSearch;
        Assert.Equal("Installed 3.12.0 · dependency", Find(page, "SML").InstalledText);
    }

    [Fact]
    public async Task UninstallRemovesTheModAndItsUnneededDependencies()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: new VersionDependency("SML", "^3.12.0", false));
        var page = await OpenPageAsync();
        page.SelectedMod = Find(page, "A");
        await page.InstallCommand.ExecuteAsync(null);

        page.SelectedMod = Find(page, "A");
        await page.UninstallCommand.ExecuteAsync(null);
        Assert.Equal("Removed A, SML.", page.Notice);

        Assert.Empty(new ModInstaller(_game.Root, () => false).Installed);
        Assert.False(Find(page, "A").IsInstalled);
    }

    [Fact]
    public async Task QuickInstallUsesTheClickedModAndLatestVersionWithoutOpeningItsPage()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.1.0", "Rain", new VersionDependency("SML", "^3.12.0", false));
        var page = await OpenPageAsync();
        page.SearchText = "rain";
        await page.PendingSearch;
        page.IsGridView = true;
        var mod = Assert.Single(page.Results);
        mod.SelectedVersionChoice = new Option<string?>("1.0.0", "1.0.0");
        Assert.Equal("Install", mod.QuickActionText);

        await page.QuickChangeCommand.ExecuteAsync(mod);

        Assert.False(page.HasSelection);
        Assert.Null(page.ListSelection);
        Assert.Equal("rain", page.SearchText);
        Assert.True(page.IsGridView);
        Assert.Equal("1.1.0", Find(page, "A").InstalledVersion);
        Assert.Equal("Uninstall", Find(page, "A").QuickActionText);
        Assert.NotNull(_game.Read("Mods/SML"));
        Assert.False(page.IsBusy);
        Assert.Null(page.ProgressText);
    }

    [Fact]
    public async Task QuickUninstallRemovesUnneededDependenciesAndRefreshesTheButtonLabel()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: new VersionDependency("SML", "^3.12.0", false));
        var page = await OpenPageAsync();
        await page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));

        await page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));

        Assert.Empty(new ModInstaller(_game.Root, () => false).Installed);
        Assert.Equal("Install", Find(page, "A").QuickActionText);
        Assert.Equal("Removed A, SML.", page.Notice);
        Assert.False(page.HasSelection);
        Assert.Null(page.ListSelection);
    }

    [Fact]
    public async Task QuickUninstallCannotRemoveADependencyNeededByAnInstalledMod()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: new VersionDependency("SML", "^3.12.0", false));
        var page = await OpenPageAsync();
        await page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));

        await page.QuickChangeCommand.ExecuteAsync(Find(page, "SML"));

        Assert.Equal(["A", "SML"], new ModInstaller(_game.Root, () => false).Installed.Keys.Order());
        Assert.False(string.IsNullOrWhiteSpace(page.Notice));
        Assert.Equal("Uninstall", Find(page, "SML").QuickActionText);
        Assert.False(page.HasSelection);
        Assert.False(page.IsBusy);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("no game")]
    public async Task QuickChangesAreDisabledAndNotifyButtonsWhenThePageCannotApplyChanges(string state)
    {
        Publish("SML", "3.12.0");
        var page = await OpenPageAsync();
        var mod = Find(page, "SML");
        Assert.True(page.QuickChangeCommand.CanExecute(mod));
        Assert.False(page.QuickChangeCommand.CanExecute(null));
        var notifications = 0;
        page.QuickChangeCommand.CanExecuteChanged += (_, _) => notifications++;

        switch (state)
        {
            case "running":
                page.IsGameRunning = true;
                break;
            case "no game":
                page.SelectedGame = null;
                await page.RefreshAsync();
                break;
        }

        Assert.True(notifications > 0);
        Assert.False(page.QuickChangeCommand.CanExecute(mod));
        await page.QuickChangeCommand.ExecuteAsync(mod);
        Assert.Null(_game.Read("Mods/SML"));
        Assert.False(page.HasSelection);
    }

    [Fact]
    public async Task ResolutionProblemsPreventInstallationAndShowANotice()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: new VersionDependency("SML", "^3.13.0", false));
        var page = await OpenPageAsync();
        page.SelectedMod = Find(page, "A");

        await page.InstallCommand.ExecuteAsync(null);

        Assert.Empty(new ModInstaller(_game.Root, () => false).Installed);
        Assert.False(page.IsBusy);
        Assert.Contains("needs SML ^3.13.0", page.Notice);
    }

    [Fact]
    public async Task InstalledOnlyListsJustTheModsInTheSelectedGame()
    {
        Publish("SML", "3.12.0");
        Publish("Other", "1.0.0");
        var page = await OpenPageAsync();
        page.SelectedMod = Find(page, "SML");
        await page.InstallCommand.ExecuteAsync(null);

        page.Filter = ModListQuery.Filters.Single(f => f.Value == ModFilter.Installed);
        await page.PendingSearch;

        Assert.Equal(["SML"], page.Results.Select(r => r.Reference));
    }

    [Fact]
    public async Task QueueForInstallAddsModsAndShowsTheQueue()
    {
        Publish("A", "1.0.0");
        Publish("B", "1.0.0");
        var page = await OpenPageAsync();

        page.QueueForInstall(["A", "B"]);
        await page.PendingSearch;

        Assert.Equal("2 mods queued", page.QueueText);
        Assert.Equal(ModFilter.Queued, page.Filter.Value);
        Assert.Equal(["A", "B"], page.Results.Select(r => r.Reference).Order());
        Assert.All(page.Results, r => Assert.True(r.IsQueued));
    }

    [Fact]
    public async Task QueuedModsInstallTogetherWithoutConfirmation()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: new VersionDependency("SML", "^3.12.0", false));
        Publish("B", "1.0.0", deps: new VersionDependency("SML", "^3.12.0", false));
        var page = await OpenPageAsync();

        page.SelectedMod = Find(page, "A");
        page.ToggleQueueCommand.Execute(null);
        page.SelectedMod = Find(page, "B");
        page.ToggleQueueCommand.Execute(null);
        Assert.Equal("2 mods queued", page.QueueText);

        page.Filter = ModListQuery.Filters.Single(f => f.Value == ModFilter.Queued);
        await page.PendingSearch;
        Assert.Equal(["A", "B"], page.Results.Select(r => r.Reference).Order());

        await page.InstallQueuedCommand.ExecuteAsync(null);

        Assert.Equal(["A", "B", "SML"], new ModInstaller(_game.Root, () => false).Installed.Keys.Order());
        Assert.False(page.HasQueue);
    }

    [Fact]
    public async Task LayoutSortAndFilterAreRememberedBetweenLaunches()
    {
        Publish("SML", "3.12.0");
        var page = await OpenPageAsync();

        page.IsCardView = true;
        page.Sort = ModListQuery.Sorts.Single(o => o.Value == ModSort.Views);
        page.Filter = ModListQuery.Filters.Single(o => o.Value == ModFilter.NotInstalled);
        await page.PendingChoicesSave;

        var reopened = await OpenPageAsync();

        Assert.Equal(ModView.Card, reopened.View);
        Assert.Equal(ModSort.Views, reopened.Sort.Value);
        Assert.Equal(ModFilter.NotInstalled, reopened.Filter.Value);
    }

    [Fact]
    public async Task ARememberedQueueFilterOpensOnAllSinceTheQueueIsForOneSession()
    {
        Settings.Save(new AppSettings(ModsFilter: nameof(ModFilter.Queued), ModsView: "Nonsense"));

        var page = await OpenPageAsync();

        Assert.Equal(ModFilter.All, page.Filter.Value);
        Assert.Equal(ModView.List, page.View);
    }

    [Fact]
    public async Task TheSettingsPageKeepsTheModsPageChoices()
    {
        Publish("SML", "3.12.0");
        var page = await OpenPageAsync();
        page.IsGridView = true;
        await page.PendingChoicesSave;

        var store = Settings;
        var settings = new SettingsViewModel(store, new ArchiveCache(_dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance);
        settings.KeepDownloadedArchives = false;
        await settings.PendingSave;

        Assert.Equal(nameof(ModView.Grid), store.Load().ModsView);
        Assert.False(store.Load().KeepDownloadedArchives);
    }

    [Fact]
    public async Task FavouritesAreSavedAndHaveTheirOwnFilter()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0");
        var page = await OpenPageAsync();

        Find(page, "A").ToggleFavouriteCommand.Execute(null);
        page.Filter = ModListQuery.Filters.Single(f => f.Value == ModFilter.Favourite);
        await page.PendingSearch;

        Assert.Equal(["A"], page.Results.Select(r => r.Reference));
        Assert.Equal("★", page.Results[0].FavouriteGlyph);
        Assert.Contains("A", new FavouritesStore(_dir.Combine("favourites.json")).All);
    }

    [Fact]
    public async Task ChoosingAModOpensItsPageAndBackReturnsToTheList()
    {
        Publish("SML", "3.12.0");
        Publish("SML", "3.12.5");
        Publish("A", "1.0.0", "A mod ![shot](https://example.com/a.png)", new VersionDependency("SML", "^3.12.0", false));
        var page = await OpenPageAsync();

        page.ListSelection = Find(page, "A");
        await page.PendingDetails;

        Assert.True(page.HasSelection);
        Assert.Null(page.ListSelection);
        var mod = page.SelectedMod!;
        Assert.Equal("Latest for this game: 1.0.0", mod.LatestText);
        Assert.Equal("SML", Assert.Single(mod.Dependencies).Reference);
        Assert.False(mod.Dependencies[0].IsInstalled);
        Assert.Equal("https://example.com/a.png", Assert.Single(mod.Gallery).Url);
        Assert.Equal(["Latest compatible", "1.0.0"], mod.VersionChoices.Select(c => c.Label));
        Assert.True(mod.StableCompatibility.IsCompatible);
        Assert.False(mod.ExperimentalCompatibility.IsCompatible);

        page.BackCommand.Execute(null);
        Assert.False(page.HasSelection);
    }

    [Fact]
    public async Task APickedVersionIsInstalledAndCanBeSwitched()
    {
        Publish("SML", "3.12.0");
        Publish("SML", "3.12.5");
        var page = await OpenPageAsync();
        page.ListSelection = Find(page, "SML");
        await page.PendingDetails;

        page.SelectedMod!.SelectedVersionChoice = page.SelectedMod.VersionChoices.Single(c => c.Value == "3.12.0");
        Assert.Equal("Install 3.12.0", page.SelectedMod.InstallText);
        await page.InstallCommand.ExecuteAsync(null);
        await page.PendingDetails;
        Assert.Equal("3.12.0", new ModInstaller(_game.Root, () => false).Installed["SML"].Version);

        page.SelectedMod.SelectedVersionChoice = page.SelectedMod.VersionChoices.Single(c => c.Value == "3.12.5");
        Assert.Equal("Switch to 3.12.5", page.SelectedMod.InstallText);
        await page.InstallCommand.ExecuteAsync(null);

        Assert.Equal("3.12.5", new ModInstaller(_game.Root, () => false).Installed["SML"].Version);
    }

    [Fact]
    public async Task TheOpenPageStaysWhenItsModLeavesTheFilteredList()
    {
        Publish("SML", "3.12.0");
        var page = await OpenPageAsync();
        page.ListSelection = Find(page, "SML");
        await page.InstallCommand.ExecuteAsync(null);
        page.Filter = ModListQuery.Filters.Single(f => f.Value == ModFilter.Installed);
        await page.PendingSearch;

        await page.UninstallCommand.ExecuteAsync(null);

        Assert.True(page.HasSelection);
        Assert.False(page.SelectedMod!.IsInstalled);
        Assert.Empty(page.Results);
    }

    [Fact]
    public async Task EachInstallHasItsOwnRunningStateAndAServerIsNamed()
    {
        Publish("SML", "3.12.0");
        var other = new GameFolder(_dir.Combine("Other"));
        var page = await OpenPageAsync(otherGame: other);
        page.SelectedMod = Find(page, "SML");

        page.Games[1].IsGameRunning = true;
        Assert.False(page.IsGameRunning);
        Assert.True(page.InstallCommand.CanExecute(null));

        page.SelectedGame = page.Games[1];
        Assert.True(page.IsGameRunning);
        Assert.Equal("Satisfactory is running. Close it to install or uninstall mods.", page.GameRunningText);

        var installs = new ModInstallService(Settings, new(new HttpClient(_http), _dir.Combine("Archives")),
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), (_, _) => Task.FromResult(false));
        var profiles = new GameProfiles(new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json")), installs);
        var server = new GameInstallViewModel(new GameInstall("sftp://abc", GameLauncher.Unknown, GameHost.Linux, GameKind.DedicatedServer,
                null, "Home", new GameBuild(513885, "1.2.4.1")), installs,
            new ModResolutionService(new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), installs),
            profiles, NullLogger<GameInstallViewModel>.Instance) { IsGameRunning = true };
        page.Games.Add(server);
        page.SelectedGame = server;
        page.SelectedMod = Find(page, "SML");

        Assert.Equal("Home is running. Stop the server to install or uninstall mods.", page.GameRunningText);
        server.IsGameRunning = false;
        server.UnavailableReason = "Couldn't reach it.";
        Assert.Equal("Home can't be used right now: Couldn't reach it.", page.UnavailableText);
        Assert.False(page.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task AModWithoutABuildForTheSelectedServerIsntOfferedForInstall()
    {
        Publish("BetterGrass", "2.0.0");
        _store.Upsert([CatalogFixtures.Mod("BetterGrass",
            latest: [new LatestVersion(VersionStability.Release, "2.0.0", ">=513885", [TargetPlatform.Windows])])], DateTimeOffset.UtcNow);
        var page = await OpenPageAsync();
        var mod = Find(page, "BetterGrass");
        var local = page.SelectedGame!;
        Assert.False(mod.HasNoBuildHere);

        var installs = new ModInstallService(Settings, new(new HttpClient(_http), _dir.Combine("Archives")),
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), (_, _) => Task.FromResult(false));
        var profiles = new GameProfiles(new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json")), installs);
        var server = new GameInstallViewModel(new GameInstall("sftp://abc", GameLauncher.Unknown, GameHost.Linux, GameKind.DedicatedServer,
                null, "Home", new GameBuild(513885, "1.2.4.1")), installs,
            new ModResolutionService(new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), installs),
            profiles, NullLogger<GameInstallViewModel>.Instance);
        page.Games.Add(server);
        page.SelectedGame = server;
        page.SelectedMod = mod;
        await Wait.Until(() => mod.HasNoBuildHere);

        Assert.Equal("No server build", mod.NoBuildHere!.Tag);
        Assert.Contains("No Linux server build", mod.NoBuildHere.Reason);
        Assert.False(mod.CanInstallHere);
        Assert.False(mod.ShowQuickAction);
        Assert.False(page.InstallCommand.CanExecute(null));
        Assert.False(page.QuickChangeCommand.CanExecute(mod));
        Assert.False(page.ToggleQueueCommand.CanExecute(null));

        page.SelectedGame = local;
        await Wait.Until(() => !mod.HasNoBuildHere);
        Assert.True(page.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheQuickActionIsRefusedForAnUnavailableServer()
    {
        Publish("SML", "3.12.0");
        var page = await OpenPageAsync();

        page.SelectedGame!.UnavailableReason = "Couldn't reach it.";

        Assert.False(page.QuickChangeCommand.CanExecute(Find(page, "SML")));
    }

    [Fact]
    public async Task ChangesAreBlockedWhileTheGameRunsIncludingALateStart()
    {
        Publish("SML", "3.12.0");
        DashboardViewModel? dashboard = null;
        var page = await OpenPageAsync(dashboardReady: value => dashboard = value);
        page.SelectedMod = Find(page, "SML");
        // Each install has its own running state; the process watcher sets it and the page follows the selected one.
        page.SelectedGame!.IsGameRunning = true;
        Assert.True(page.IsGameRunning);
        Assert.False(page.InstallCommand.CanExecute(null));
        Assert.False(page.UninstallCommand.CanExecute(null));
        page.ToggleQueueCommand.Execute(null);
        Assert.False(page.InstallQueuedCommand.CanExecute(null));

        // The game started between the check and the click: the install engine refuses, the page explains.
        page.SelectedGame.IsGameRunning = false;
        _gameRunning = true;
        var installing = page.InstallCommand.ExecuteAsync(null);
        await page.PendingOperations.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("running", page.GameRunningText);
        Assert.Null(_game.Read("Mods/SML"));
        Assert.False(page.IsBusy);
        Assert.Null(page.ProgressText);
        Assert.Equal(1, page.PendingOperationCount);
        Assert.False(installing.IsCompleted);

        Assert.True(dashboard!.IsGameRunning);
        Assert.All(page.Games, game => Assert.True(game.IsGameRunning));
        _gameRunning = false;
        dashboard.IsGameRunning = false;
        foreach (var game in page.Games)
        {
            game.IsGameRunning = false;
        }
        await installing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(_game.Read("Mods/SML"));
        Assert.False(page.HasPendingOperations);
    }

    [Fact]
    public async Task ChangingGameDuringPlanningKeepsTheOriginalTargetAndEachGamesQueue()
    {
        Publish("SML", "3.12.0");
        var otherGame = new GameFolder(_dir.Combine("OtherSatisfactory"));
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<CatalogVersion>> DelayedVersions(string reference, CancellationToken ct)
        {
            lookupStarted.TrySetResult();
            await continueLookup.Task.WaitAsync(ct);
            return _versions[reference];
        }

        var page = await OpenPageAsync(DelayedVersions, otherGame);
        var originalGame = page.SelectedGame;
        page.SelectedMod = Find(page, "SML");
        page.ToggleQueueCommand.Execute(null);
        var installing = page.InstallQueuedCommand.ExecuteAsync(null);
        try
        {
            await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(page.IsBusy);
            Assert.False(page.InstallCommand.CanExecute(null));
            Assert.False(page.UninstallCommand.CanExecute(null));
            Assert.False(page.InstallQueuedCommand.CanExecute(null));

            page.SelectedGame = page.Games.Single(g => g != originalGame);
            await page.RefreshAsync();
            page.SelectedMod = Find(page, "SML");
            page.ToggleQueueCommand.Execute(null);
        }
        finally
        {
            continueLookup.TrySetResult();
            await installing;
        }

        Assert.NotNull(_game.Read("Mods/SML"));
        Assert.Null(otherGame.Read("Mods/SML"));
        Assert.False(page.IsBusy);
        Assert.False(page.SelectedMod!.IsInstalled);
        Assert.Equal(1, page.QueueCount);

        page.SelectedGame = originalGame;
        await page.RefreshAsync();
        Assert.False(page.HasQueue);
        Assert.True(Find(page, "SML").IsInstalled);
    }

    [Fact]
    public async Task BusyButtonsEnqueueMixedOperationsInClickOrderAndIgnoreDuplicateRequests()
    {
        Publish("A", "1.0.0");
        Publish("B", "1.0.0");
        Publish("X", "1.0.0");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = false;
        async Task<IReadOnlyList<CatalogVersion>> Versions(string reference, CancellationToken ct)
        {
            if (delay && reference == "A")
            {
                started.TrySetResult();
                await resume.Task.WaitAsync(ct);
            }
            if (delay && reference == "B")
            {
                Assert.Null(_game.Read("Mods/X"));
            }
            return _versions.GetValueOrDefault(reference) ?? [];
        }

        var page = await OpenPageAsync(Versions);
        await page.QuickChangeCommand.ExecuteAsync(Find(page, "X"));
        delay = true;
        var a = Find(page, "A");
        var b = Find(page, "B");
        var x = Find(page, "X");
        var installing = page.QuickChangeCommand.ExecuteAsync(a);
        var removing = Task.CompletedTask;
        var installingB = Task.CompletedTask;
        var duplicate = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Installing…", a.QuickActionText);
            Assert.False(page.QuickChangeCommand.CanExecute(a));
            Assert.True(page.QuickChangeCommand.CanExecute(b));
            Assert.True(page.QuickChangeCommand.CanExecute(x));
            Assert.Equal("Queue install", b.QuickActionText);
            Assert.Equal("Queue uninstall", x.QuickActionText);

            removing = page.QuickChangeCommand.ExecuteAsync(x);
            installingB = page.QuickChangeCommand.ExecuteAsync(b);
            duplicate = page.QuickChangeCommand.ExecuteAsync(b);
            Assert.Equal(2, page.PendingOperationCount);
            Assert.Equal("Uninstall queued · #2", x.QuickActionText);
            Assert.Equal("Install queued · #3", b.QuickActionText);
            Assert.True(x.IsUninstallAction);
            Assert.False(b.IsUninstallAction);
            Assert.NotNull(_game.Read("Mods/X"));
            Assert.Null(_game.Read("Mods/B"));

            page.SearchText = "B";
            await page.PendingSearch;
            Assert.Equal("Install queued · #3", Find(page, "B").QuickActionText);
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(installing, removing, installingB, duplicate).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(["A", "B"], new ModInstaller(_game.Root, () => false).Installed.Keys.Order());
        Assert.True(page.Notice!.IndexOf("Installed A", StringComparison.Ordinal) < page.Notice.IndexOf("Removed X", StringComparison.Ordinal));
        Assert.True(page.Notice.IndexOf("Removed X", StringComparison.Ordinal) < page.Notice.IndexOf("Installed B", StringComparison.Ordinal));
        Assert.False(page.HasPendingOperations);
        Assert.False(page.IsBusy);
        Assert.False(page.HasSelection);
        Assert.Equal("Uninstall", Find(page, "B").QuickActionText);
    }

    [Fact]
    public async Task ACapturedInstallDoesNotBecomeAnUninstallWhenAnEarlierOperationInstallsItsDependency()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: new VersionDependency("SML", "^3.12.0", false));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<CatalogVersion>> Versions(string reference, CancellationToken ct)
        {
            if (reference == "A")
            {
                started.TrySetResult();
                await resume.Task.WaitAsync(ct);
            }
            return _versions.GetValueOrDefault(reference) ?? [];
        }

        var page = await OpenPageAsync(Versions);
        var installing = page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));
        var keepLoader = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            keepLoader = page.QuickChangeCommand.ExecuteAsync(Find(page, "SML"));
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(installing, keepLoader).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.NotNull(_game.Read("Mods/SML"));
        Assert.True(Find(page, "SML").IsInstalled);
        Assert.False(Find(page, "SML").IsDependencyOnly);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFailedOperationKeepsItsNoticeAndAllowsLaterWorkToRun(bool throws)
    {
        Publish("A", "1.0.0", deps: new VersionDependency("Missing", "^1.0.0", false));
        Publish("B", "1.0.0");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<CatalogVersion>> Versions(string reference, CancellationToken ct)
        {
            if (reference == "A")
            {
                started.TrySetResult();
                await resume.Task.WaitAsync(ct);
                if (throws)
                {
                    throw new IOException("Offline for A");
                }
            }
            return _versions.GetValueOrDefault(reference) ?? [];
        }

        var page = await OpenPageAsync(Versions);
        var installing = page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));
        var installingB = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            installingB = page.QuickChangeCommand.ExecuteAsync(Find(page, "B"));
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(installing, installingB).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Null(_game.Read("Mods/A"));
        Assert.NotNull(_game.Read("Mods/B"));
        Assert.Contains(throws ? "Offline for A" : "Missing", page.Notice);
        Assert.Contains("Installed B 1.0.0.", page.Notice);
        Assert.False(page.IsBusy);
        Assert.False(page.HasPendingOperations);
    }

    [Fact]
    public async Task QueuedDetailsCaptureThePickedVersionAndGameEvenAfterBothSelectionsChange()
    {
        Publish("A", "1.0.0");
        Publish("B", "1.0.0");
        Publish("B", "2.0.0");
        Publish("C", "1.0.0");
        var otherGame = new GameFolder(_dir.Combine("OtherSatisfactory"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<CatalogVersion>> Versions(string reference, CancellationToken ct)
        {
            if (reference == "A")
            {
                started.TrySetResult();
                await resume.Task.WaitAsync(ct);
            }
            return _versions.GetValueOrDefault(reference) ?? [];
        }

        var page = await OpenPageAsync(Versions, otherGame);
        var originalGame = page.SelectedGame;
        var installing = page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));
        var installingB = Task.CompletedTask;
        var installingC = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.SelectedMod = Find(page, "B");
            await page.PendingDetails;
            page.SelectedMod.SelectedVersionChoice = page.SelectedMod.VersionChoices.Single(c => c.Value == "1.0.0");
            Assert.True(page.InstallCommand.CanExecute(null));
            Assert.Equal("Queue install 1.0.0", page.SelectedMod.InstallText);
            installingB = page.InstallCommand.ExecuteAsync(null);
            page.SelectedMod.SelectedVersionChoice = page.SelectedMod.VersionChoices.Single(c => c.Value == "2.0.0");
            Assert.Equal("Install queued · #2", page.SelectedMod.InstallText);

            page.SelectedGame = page.Games.Single(g => g != originalGame);
            await page.RefreshAsync();
            installingC = page.QuickChangeCommand.ExecuteAsync(Find(page, "C"));
            Assert.Equal(2, page.PendingOperationCount);
            page.SelectedGame = originalGame;
            await page.RefreshAsync();
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(installing, installingB, installingC).WaitAsync(TimeSpan.FromSeconds(10));
        }

        var original = new ModInstaller(_game.Root, () => false).Installed;
        Assert.Equal(["A", "B"], original.Keys.Order());
        Assert.Equal("1.0.0", original["B"].Version);
        Assert.Equal(["C"], new ModInstaller(otherGame.Root, () => false).Installed.Keys);
        Assert.False(page.IsBusy);
    }

    [Fact]
    public async Task AnExistingInstallBatchJoinsTheExecutionQueueAsOneCapturedOperation()
    {
        Publish("A", "1.0.0");
        Publish("B", "1.0.0");
        Publish("C", "1.0.0");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<CatalogVersion>> Versions(string reference, CancellationToken ct)
        {
            if (reference == "A")
            {
                started.TrySetResult();
                await resume.Task.WaitAsync(ct);
            }
            return _versions.GetValueOrDefault(reference) ?? [];
        }

        var page = await OpenPageAsync(Versions);
        var installing = page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));
        var batch = Task.CompletedTask;
        var duplicate = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.SelectedMod = Find(page, "B");
            page.ToggleQueueCommand.Execute(null);
            page.SelectedMod = Find(page, "C");
            page.ToggleQueueCommand.Execute(null);
            Assert.True(page.InstallQueuedCommand.CanExecute(null));
            Assert.Equal("Queue installs", page.InstallQueuedButtonText);
            batch = page.InstallQueuedCommand.ExecuteAsync(null);
            duplicate = page.QuickChangeCommand.ExecuteAsync(Find(page, "B"));
            Assert.Equal(1, page.PendingOperationCount);
            Assert.Equal("Install queued · #2", Find(page, "B").QuickActionText);
            Assert.Equal("Install queued · #2", Find(page, "C").QuickActionText);
            Assert.False(page.InstallQueuedCommand.CanExecute(null));
            page.ClearQueueCommand.Execute(null);
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(installing, batch, duplicate).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(["A", "B", "C"], new ModInstaller(_game.Root, () => false).Installed.Keys.Order());
        Assert.Contains("Installed B 1.0.0, C 1.0.0.", page.Notice);
        Assert.False(page.HasQueue);
    }

    [Fact]
    public async Task StartingTheGameDuringPlanningPausesRequestsInOrderAndClosingItResumesThem()
    {
        Publish("A", "1.0.0");
        Publish("B", "1.0.0");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<CatalogVersion>> Versions(string reference, CancellationToken ct)
        {
            if (reference == "A")
            {
                started.TrySetResult();
                await resume.Task.WaitAsync(ct);
            }
            return _versions.GetValueOrDefault(reference) ?? [];
        }

        var page = await OpenPageAsync(Versions);
        var installing = page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));
        var installingB = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            installingB = page.QuickChangeCommand.ExecuteAsync(Find(page, "B"));
            page.SelectedGame!.IsGameRunning = true;
            resume.TrySetResult();
            await page.PendingOperations.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(page.IsBusy);
            Assert.Equal(2, page.PendingOperationCount);
            Assert.Contains("until Satisfactory closes", page.OperationQueueText);
            Assert.Equal("Install queued · #1", Find(page, "A").QuickActionText);
            Assert.Equal("Install queued · #2", Find(page, "B").QuickActionText);
            Assert.False(installing.IsCompleted);
            Assert.False(installingB.IsCompleted);
            Assert.Null(_game.Read("Mods/A"));
            Assert.Null(_game.Read("Mods/B"));
        }
        finally
        {
            resume.TrySetResult();
            page.SelectedGame!.IsGameRunning = false;
            await Task.WhenAll(installing, installingB).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(["A", "B"], new ModInstaller(_game.Root, () => false).Installed.Keys.Order());
        Assert.False(page.HasPendingOperations);
        Assert.False(page.IsBusy);
    }

    [Fact]
    public async Task ACapturedRemovalDoesNotBecomeAnInstallAfterEarlierDependencyCleanup()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: new VersionDependency("SML", "^3.12.0", false));
        Publish("B", "1.0.0");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = false;
        async Task<IReadOnlyList<CatalogVersion>> Versions(string reference, CancellationToken ct)
        {
            if (delay && reference == "B")
            {
                started.TrySetResult();
                await resume.Task.WaitAsync(ct);
            }
            return _versions.GetValueOrDefault(reference) ?? [];
        }

        var page = await OpenPageAsync(Versions);
        await page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));
        await page.QuickChangeCommand.ExecuteAsync(Find(page, "B"));
        delay = true;
        var removing = page.QuickChangeCommand.ExecuteAsync(Find(page, "A"));
        var removeLoader = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.SelectedMod = Find(page, "SML");
            Assert.True(page.UninstallCommand.CanExecute(null));
            Assert.Equal("Queue uninstall", page.SelectedMod.UninstallText);
            removeLoader = page.UninstallCommand.ExecuteAsync(null);
            Assert.Equal("Uninstall queued · #2", page.SelectedMod.UninstallText);
        }
        finally
        {
            resume.TrySetResult();
            await Task.WhenAll(removing, removeLoader).WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(["B"], new ModInstaller(_game.Root, () => false).Installed.Keys);
        Assert.False(page.SelectedMod!.ShowUninstall);
        Assert.Null(page.SelectedMod.PendingUninstall);
    }

    [Fact]
    public async Task EndlessScrollReachesEveryMatchOnceAndKeepsExistingItems()
    {
        AddBrowsingMods(233);
        var page = await OpenPageAsync();
        var first = page.Results[0];

        Assert.False(page.IsPaged);
        Assert.Equal(50, page.Results.Count);
        var seen = new List<string>(page.Results.Select(m => m.Reference));
        while (page.LoadMoreCommand.CanExecute(null))
        {
            var previousCount = page.Results.Count;
            page.LoadMoreCommand.Execute(null);
            Assert.InRange(page.Results.Count - previousCount, 1, 50);
            seen.AddRange(page.Results.Skip(previousCount).Select(m => m.Reference));
        }

        Assert.Same(first, page.Results[0]);
        Assert.Equal(233, seen.Count);
        Assert.Equal(233, seen.Distinct().Count());
        Assert.Equal(_store.Search("", int.MaxValue).Select(m => m.Reference), seen);
        Assert.False(page.HasMoreResults);
        page.LoadMoreCommand.Execute(null);
        Assert.Equal(233, page.Results.Count);
        Assert.Equal("233 mods", page.ResultsText);
    }

    [Fact]
    public async Task ExtendingResultsUsesTheSnapshotUntilARefresh()
    {
        AddBrowsingMods(120);
        var page = await OpenPageAsync();
        _store.Upsert([CatalogFixtures.Mod("New", popularity: int.MaxValue)], DateTimeOffset.UtcNow);

        page.LoadMoreCommand.Execute(null);

        Assert.Equal(100, page.Results.Count);
        Assert.DoesNotContain(page.Results, m => m.Reference == "New");
        await page.RefreshAsync();
        Assert.Equal(100, page.Results.Count);
        Assert.Equal("New", page.Results[0].Reference);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(500)]
    public async Task PaginationVisitsEveryMatchInOrderAtEachSupportedSize(int size)
    {
        AddBrowsingMods(size * 2 + 7);
        Settings.Save(new AppSettings(EndlessScroll: false, ModsPageSize: size));
        var page = await OpenPageAsync();
        var seen = new List<string>();

        Assert.True(page.IsPaged);
        Assert.False(page.PreviousPageCommand.CanExecute(null));
        Assert.False(page.LoadMoreCommand.CanExecute(null));
        Assert.Equal(3, page.TotalPages);
        for (var number = 1; number <= 3; number++)
        {
            Assert.Equal(number, page.CurrentPage);
            Assert.Equal(number == 3 ? 7 : size, page.Results.Count);
            Assert.Equal($"Page {number} of 3", page.PageText);
            seen.AddRange(page.Results.Select(m => m.Reference));
            if (number < 3)
            {
                Assert.True(page.NextPageCommand.CanExecute(null));
                page.NextPageCommand.Execute(null);
            }
        }

        Assert.Equal(_store.Search("", int.MaxValue).Select(m => m.Reference), seen);
        Assert.False(page.NextPageCommand.CanExecute(null));
        page.NextPageCommand.Execute(null);
        Assert.Equal(3, page.CurrentPage);
        page.PreviousPageCommand.Execute(null);
        Assert.Equal(2, page.CurrentPage);
        Assert.Equal(seen.Skip(size).Take(size), page.Results.Select(m => m.Reference));
        Assert.Equal($"{size + 1:N0}–{size * 2:N0} of {size * 2 + 7:N0} mods", page.ResultsText);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(50, 1)]
    [InlineData(100, 2)]
    public async Task PaginationHandlesEmptyAndExactPages(int count, int pages)
    {
        AddBrowsingMods(count);
        Settings.Save(new AppSettings(EndlessScroll: false));
        var page = await OpenPageAsync();

        Assert.Equal(pages, page.TotalPages);
        Assert.Equal(Math.Min(50, count), page.Results.Count);
        Assert.False(page.PreviousPageCommand.CanExecute(null));
        Assert.Equal(pages > 1, page.NextPageCommand.CanExecute(null));
        if (count == 0)
        {
            Assert.Equal("No pages", page.PageText);
            Assert.Contains("hasn't been downloaded", page.ResultsText);
        }
    }

    [Fact]
    public async Task NewSearchesDisableOldNavigationImmediatelyAndResetTheWindow()
    {
        AddBrowsingMods(231);
        var page = await OpenPageAsync();
        page.LoadMoreCommand.Execute(null);
        page.SearchText = "odd";

        Assert.True(page.IsSearching);
        Assert.False(page.LoadMoreCommand.CanExecute(null));
        page.LoadMoreCommand.Execute(null);
        Assert.Equal(100, page.Results.Count);
        page.SearchText = "even";
        await page.PendingSearch;

        Assert.Equal(50, page.Results.Count);
        Assert.All(page.Results, m => Assert.Contains("even", m.Mod.Tags));
        page.LoadMoreCommand.Execute(null);
        page.Sort = ModListQuery.Sorts.Single(o => o.Value == ModSort.Name);
        await page.PendingSearch;
        Assert.Equal(50, page.Results.Count);
        page.LoadMoreCommand.Execute(null);
        page.Tags.Single(t => t.Name == "even").IsSelected = true;
        await page.PendingSearch;
        Assert.Equal(50, page.Results.Count);
        page.Filter = ModListQuery.Filters.Single(o => o.Value == ModFilter.Installed);
        await page.PendingSearch;
        Assert.Empty(page.Results);
        Assert.False(page.HasMoreResults);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetailsAndRefreshPreserveTheBrowsingWindowAndOffset(bool endless)
    {
        AddBrowsingMods(233);
        Settings.Save(new AppSettings(EndlessScroll: endless));
        var page = await OpenPageAsync();
        if (endless)
        {
            page.LoadMoreCommand.Execute(null);
            page.LoadMoreCommand.Execute(null);
        }
        else
        {
            page.NextPageCommand.Execute(null);
        }
        var references = page.Results.Select(m => m.Reference).ToList();
        page.ResultsScrollOffset = 1234;
        page.SelectedMod = page.Results[20];
        await page.PendingDetails;
        page.BackCommand.Execute(null);
        await page.RefreshAsync();

        Assert.Equal(references, page.Results.Select(m => m.Reference));
        Assert.Equal(1234, page.ResultsScrollOffset);
        Assert.Equal(endless ? 1 : 2, page.CurrentPage);
    }

    [Fact]
    public async Task DelayedBrowsingSavesApplyAfterReturningToModsAndPreserveOtherChoices()
    {
        AddBrowsingMods(233);
        var store = Settings;
        var page = await OpenPageAsync(settingsStore: store);
        page.IsCardView = true;
        await page.PendingChoicesSave;
        page.LoadMoreCommand.Execute(null);
        var settings = new SettingsViewModel(store, new ArchiveCache(_dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance)
        {
            EndlessScroll = false,
            ModsPageSize = 25,
        };
        await page.RefreshAsync();
        await settings.PendingSave;
        await page.PendingBrowsingUpdate;

        Assert.True(page.IsPaged);
        Assert.Equal(25, page.Results.Count);
        page.NextPageCommand.Execute(null);
        settings.ModsPageSize = 200;
        await settings.PendingSave;
        await page.PendingBrowsingUpdate;
        Assert.Equal(1, page.CurrentPage);
        Assert.Equal(200, page.Results.Count);
        settings.EndlessScroll = true;
        await settings.PendingSave;
        await page.PendingBrowsingUpdate;
        Assert.False(page.IsPaged);
        Assert.Equal(50, page.Results.Count);
        page.LoadMoreCommand.Execute(null);
        settings.ModsPageSize = 500;
        await settings.PendingSave;
        await page.PendingBrowsingUpdate;
        Assert.Equal(100, page.Results.Count);
        Assert.Equal(nameof(ModView.Card), store.Load().ModsView);
        Assert.Equal(500, store.Load().ModsPageSize);
        Assert.True(store.Load().EndlessScroll);
    }

    [Fact]
    public async Task RefreshClampsTheLastPageAfterTheCatalogueShrinks()
    {
        AddBrowsingMods(120);
        Settings.Save(new AppSettings(EndlessScroll: false));
        var page = await OpenPageAsync();
        page.NextPageCommand.Execute(null);
        page.NextPageCommand.Execute(null);
        page.ResultsScrollOffset = 1234;
        var now = DateTimeOffset.UtcNow.AddDays(1);
        _store.Upsert([CatalogFixtures.Mod("Remaining")], now);
        _store.DeleteNotSeenSince(now);

        await page.RefreshAsync();

        Assert.Equal(1, page.CurrentPage);
        Assert.Equal(0, page.ResultsScrollOffset);
        Assert.Equal("Remaining", Assert.Single(page.Results).Reference);
        Assert.False(page.NextPageCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangingGameResetsTheResultWindowAndScrollOffset(bool endless)
    {
        AddBrowsingMods(231);
        Settings.Save(new AppSettings(EndlessScroll: endless, ModsPageSize: 25));
        var otherGame = new GameFolder(_dir.Combine("OtherGame"));
        var page = await OpenPageAsync(otherGame: otherGame);
        if (endless)
        {
            page.LoadMoreCommand.Execute(null);
        }
        else
        {
            page.NextPageCommand.Execute(null);
        }
        page.ResultsScrollOffset = 1234;

        page.SelectedGame = page.Games[1];
        await page.PendingSearch;

        Assert.Equal(endless ? 50 : 25, page.Results.Count);
        Assert.Equal(1, page.CurrentPage);
        Assert.Equal(0, page.ResultsScrollOffset);
    }

    private void AddBrowsingMods(int count)
    {
        _store.Upsert(Enumerable.Range(0, count).Select(i => CatalogFixtures.Mod(
            $"Mod{i:D4}", popularity: count - i, tags: [i % 2 == 0 ? "even" : "odd"])), DateTimeOffset.UtcNow);
    }

    private sealed class KnownGames(GameInstall[] installs) : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => installs;
    }
}
