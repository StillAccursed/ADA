using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Overlord.Core.Catalog;
using Overlord.Core.Games;
using Overlord.Core.Settings;
using Overlord.Imaging;
using Overlord.Infrastructure.Catalog;
using Overlord.Infrastructure.Downloads;
using Overlord.Infrastructure.Ficsit;
using Overlord.Infrastructure.Installing;
using Overlord.Infrastructure.Profiles;
using Overlord.Infrastructure.Settings;
using Overlord.Platform.Detection;
using Overlord.Tests.Fixtures;
using Overlord.Tests.Installing;
using Overlord.ViewModels;

namespace Overlord.Tests.ViewModels;

/// <summary>The Mods page from search to a confirmed install and uninstall, against a fake game folder.</summary>
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
            _versions[reference] = list = [];
        list.Add(new CatalogVersion($"{reference}-{version}", reference, version, ">=513885", VersionStability.Release, false,
            DateTimeOffset.UnixEpoch,
            [new VersionTarget(TargetPlatform.Windows, link, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))], deps));
        _store.Upsert([CatalogFixtures.Mod(reference, shortDescription: description, fullDescription: description)], DateTimeOffset.UtcNow);
    }

    private SettingsStore Settings => new(_dir.Combine("Config", "settings.json"));

    private async Task<ModsViewModel> OpenPageAsync()
    {
        var install = new GameInstall(_game.Root, GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "test",
            new GameBuild(513885, "1.2.4.1"));
        Task<IReadOnlyList<CatalogVersion>> GetVersions(string r, CancellationToken _) =>
            Task.FromResult<IReadOnlyList<CatalogVersion>>(_versions.GetValueOrDefault(r) ?? []);
        var installs = new ModInstallService(
            new SettingsStore(_dir.Combine("settings.json")),
            new ArchiveDownloader(new HttpClient(_http), _dir.Combine("Archives")),
            GetVersions,
            _ => Task.FromResult(_gameRunning));
        var profiles = new GameProfiles(new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json")), installs);
        var catalog = new CatalogStatusViewModel(
            new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), _store),
            NullLogger<CatalogStatusViewModel>.Instance);
        var dashboard = new DashboardViewModel(
            new OneGame(install), catalog,
            game => new GameInstallViewModel(game, installs, new ModResolutionService(GetVersions, installs), profiles, NullLogger<GameInstallViewModel>.Instance),
            NullLogger<DashboardViewModel>.Instance,
            _ => Task.FromResult<IReadOnlyList<GameProcess>>([]));
        var page = new ModsViewModel(_store, (r, _, ct) => GetVersions(r, ct), new ModResolutionService(GetVersions, installs), installs, profiles, dashboard,
            new ModLogos(new HttpClient(new FakeHttpHandler())), new FavouritesStore(_dir.Combine("favourites.json")),
            NullLogger<ModsViewModel>.Instance, Settings);

        await dashboard.ScanCommand.ExecuteAsync(null);
        await page.RefreshAsync();
        return page;
    }

    private static ModItemViewModel Find(ModsViewModel page, string reference) =>
        page.Results.Single(r => r.Reference == reference);

    [Fact]
    public async Task Search_then_install_shows_the_plan_and_installs_dependencies_after_confirming()
    {
        Publish("SML", "3.12.0", "Mod loader");
        Publish("WeatherExtended", "1.1.0", "Rain and snow", new VersionDependency("SML", "^3.12.0", false));
        var page = await OpenPageAsync();

        page.SearchText = "rain";
        await page.PendingSearch;
        page.SelectedMod = Assert.Single(page.Results);

        await page.InstallCommand.ExecuteAsync(null);

        Assert.True(page.HasPendingPlan);
        Assert.Equal("Install SML 3.12.0 (needed by WeatherExtended 1.1.0)", page.PlanLines[0]);
        Assert.Equal("Install WeatherExtended 1.1.0", page.PlanLines[1]);
        Assert.Null(_game.Read("Mods/SML")); // nothing happens before Confirm

        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.False(page.HasPendingPlan);
        Assert.Equal("Installed SML 3.12.0, WeatherExtended 1.1.0.", page.Notice);
        Assert.NotNull(_game.Read("Mods/SML"));
        Assert.Equal("Installed 1.1.0", page.SelectedMod!.InstalledText);

        page.SearchText = "";
        await page.PendingSearch;
        Assert.Equal("Installed 3.12.0 · dependency", Find(page, "SML").InstalledText);
    }

    [Fact]
    public async Task Uninstall_removes_the_mod_and_its_unneeded_dependencies()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: new VersionDependency("SML", "^3.12.0", false));
        var page = await OpenPageAsync();
        page.SelectedMod = Find(page, "A");
        await page.InstallCommand.ExecuteAsync(null);
        await page.ConfirmCommand.ExecuteAsync(null);

        page.SelectedMod = Find(page, "A");
        await page.UninstallCommand.ExecuteAsync(null);
        Assert.Equal(["Remove A", "Remove SML"], page.PlanLines);
        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(new ModInstaller(_game.Root, () => false).Installed);
        Assert.False(Find(page, "A").IsInstalled);
    }

    [Fact]
    public async Task Problems_are_shown_instead_of_a_plan()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: new VersionDependency("SML", "^3.13.0", false));
        var page = await OpenPageAsync();
        page.SelectedMod = Find(page, "A");

        await page.InstallCommand.ExecuteAsync(null);

        Assert.False(page.HasPendingPlan);
        Assert.Contains("needs SML ^3.13.0", page.Notice);
    }

    [Fact]
    public async Task Installed_only_lists_just_the_mods_in_the_selected_game()
    {
        Publish("SML", "3.12.0");
        Publish("Other", "1.0.0");
        var page = await OpenPageAsync();
        page.SelectedMod = Find(page, "SML");
        await page.InstallCommand.ExecuteAsync(null);
        await page.ConfirmCommand.ExecuteAsync(null);

        page.Filter = ModListQuery.Filters.Single(f => f.Value == ModFilter.Installed);
        await page.PendingSearch;

        Assert.Equal(["SML"], page.Results.Select(r => r.Reference));
    }

    [Fact]
    public async Task Queued_mods_install_together_in_one_confirmed_change()
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
        Assert.Equal("Install 2 mods queued?", page.PlanTitle);
        Assert.Equal(["Install SML 3.12.0 (needed by A 1.0.0, B 1.0.0)", "Install A 1.0.0", "Install B 1.0.0"], page.PlanLines.Take(3));
        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(["A", "B", "SML"], new ModInstaller(_game.Root, () => false).Installed.Keys.Order());
        Assert.False(page.HasQueue);
    }

    [Fact]
    public async Task Layout_sort_and_filter_are_remembered_between_launches()
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
    public async Task A_remembered_queue_filter_opens_on_all_since_the_queue_is_for_one_session()
    {
        Settings.Save(new AppSettings(ModsFilter: nameof(ModFilter.Queued), ModsView: "Nonsense"));

        var page = await OpenPageAsync();

        Assert.Equal(ModFilter.All, page.Filter.Value);
        Assert.Equal(ModView.List, page.View);
    }

    [Fact]
    public async Task The_settings_page_keeps_the_mods_page_choices()
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
    public async Task Favourites_are_saved_and_have_their_own_filter()
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
    public async Task Choosing_a_mod_opens_its_page_and_Back_returns_to_the_list()
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
        Assert.Contains("1 of its 1 versions work with your game", mod.GameCompatibilityText);

        page.BackCommand.Execute(null);
        Assert.False(page.HasSelection);
    }

    [Fact]
    public async Task A_picked_version_is_installed_and_can_be_switched()
    {
        Publish("SML", "3.12.0");
        Publish("SML", "3.12.5");
        var page = await OpenPageAsync();
        page.ListSelection = Find(page, "SML");
        await page.PendingDetails;

        page.SelectedMod!.SelectedVersionChoice = page.SelectedMod.VersionChoices.Single(c => c.Value == "3.12.0");
        Assert.Equal("Install 3.12.0", page.SelectedMod.InstallText);
        await page.InstallCommand.ExecuteAsync(null);
        await page.ConfirmCommand.ExecuteAsync(null);
        await page.PendingDetails;
        Assert.Equal("3.12.0", new ModInstaller(_game.Root, () => false).Installed["SML"].Version);

        page.SelectedMod.SelectedVersionChoice = page.SelectedMod.VersionChoices.Single(c => c.Value == "3.12.5");
        Assert.Equal("Switch to 3.12.5", page.SelectedMod.InstallText);
        await page.InstallCommand.ExecuteAsync(null);
        Assert.Equal("Switch SML to 3.12.5?", page.PlanTitle);
        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("3.12.5", new ModInstaller(_game.Root, () => false).Installed["SML"].Version);
    }

    [Fact]
    public async Task The_open_page_stays_when_its_mod_leaves_the_filtered_list()
    {
        Publish("SML", "3.12.0");
        var page = await OpenPageAsync();
        page.ListSelection = Find(page, "SML");
        await page.InstallCommand.ExecuteAsync(null);
        await page.ConfirmCommand.ExecuteAsync(null);
        page.Filter = ModListQuery.Filters.Single(f => f.Value == ModFilter.Installed);
        await page.PendingSearch;

        await page.UninstallCommand.ExecuteAsync(null);
        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.True(page.HasSelection);
        Assert.False(page.SelectedMod!.IsInstalled);
        Assert.Empty(page.Results);
    }

    [Fact]
    public async Task A_planned_change_waits_while_the_game_runs()
    {
        Publish("SML", "3.12.0");
        var page = await OpenPageAsync();
        page.SelectedMod = Find(page, "SML");
        await page.InstallCommand.ExecuteAsync(null);

        page.IsGameRunning = true;
        Assert.False(page.ConfirmCommand.CanExecute(null));

        // The game started between the check and the click: the install engine refuses, the page explains.
        page.IsGameRunning = false;
        _gameRunning = true;
        await page.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("running", page.Notice);
        Assert.Null(_game.Read("Mods/SML"));
    }

    private sealed class OneGame(GameInstall install) : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => [install];
    }
}
