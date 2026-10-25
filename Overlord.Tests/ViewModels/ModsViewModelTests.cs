using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Overlord.Core.Catalog;
using Overlord.Core.Games;
using Overlord.Imaging;
using Overlord.Infrastructure.Catalog;
using Overlord.Infrastructure.Downloads;
using Overlord.Infrastructure.Ficsit;
using Overlord.Infrastructure.Installing;
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
        _store.Upsert([CatalogFixtures.Mod(reference, shortDescription: description)], DateTimeOffset.UtcNow);
    }

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
        var catalog = new CatalogStatusViewModel(
            new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), _store),
            NullLogger<CatalogStatusViewModel>.Instance);
        var dashboard = new DashboardViewModel(
            new OneGame(install), catalog,
            game => new GameInstallViewModel(game, installs, NullLogger<GameInstallViewModel>.Instance),
            NullLogger<DashboardViewModel>.Instance,
            _ => Task.FromResult<IReadOnlyList<GameProcess>>([]));
        var page = new ModsViewModel(_store, GetVersions, new ModResolutionService(GetVersions, installs), installs, dashboard,
            new ModLogos(new HttpClient(new FakeHttpHandler())), NullLogger<ModsViewModel>.Instance);

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

        page.InstalledOnly = true;
        await page.PendingSearch;

        Assert.Equal(["SML"], page.Results.Select(r => r.Reference));
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
