using System.Security.Cryptography;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.Ficsit;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

/// <summary>Mods imported from another manager often arrive without what they need (SML, libraries); the card offers to fix that.</summary>
public sealed class FixAfterImportTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;
    private readonly FakeHttpHandler _http = new();
    private readonly Dictionary<string, List<CatalogVersion>> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private ModResolutionService? _resolution;
    private GameProfiles? _profiles;

    public FixAfterImportTests() => _game = new GameFolder(_dir.Combine("Satisfactory"));

    public void Dispose() => _dir.Dispose();

    /// <summary>Lists a version on the fake ficsit.app, downloadable when <paramref name="serve"/>.</summary>
    private void Publish(string reference, string version, bool serve, params string[] needs) =>
        PublishWithChangelog(reference, version, serve, null, needs);

    private void PublishWithChangelog(string reference, string version, bool serve, string? changelog, params string[] needs)
    {
        var path = new SmodBuilder(reference, version).Typical().WriteTo(_dir.Combine("Published", reference));
        var bytes = File.ReadAllBytes(path);
        var link = $"/v1/version/{reference}-{version}/Windows/download";
        if (serve)
        {
            _http.RespondBytes(bytes, link);
        }

        if (!_catalog.TryGetValue(reference, out var list))
        {
            _catalog[reference] = list = [];
        }

        list.Add(new CatalogVersion($"{reference}-{version}", reference, version, ">=513885", VersionStability.Release, false,
            DateTimeOffset.UnixEpoch,
            [new VersionTarget(TargetPlatform.Windows, link, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))],
            needs.Select(n => new VersionDependency(n, "^3.12.0", false)).ToList(), changelog));
    }

    /// <summary>A mod folder as another manager leaves it.</summary>
    private void PlaceForeign(string reference, string version, params string[] needs)
    {
        var folder = _game.ModFolder("Mods/" + reference);
        Directory.CreateDirectory(folder);
        var plugins = string.Join(",", needs.Select(n => $$"""{ "Name": "{{n}}", "SemVersion": "^3.12.0" }"""));
        File.WriteAllText(Path.Combine(folder, reference + ".uplugin"), $$"""{ "SemVersion": "{{version}}", "Plugins": [{{plugins}}] }""");
    }

    private async Task<GameInstallViewModel> CardAsync()
    {
        Task<IReadOnlyList<CatalogVersion>> GetVersions(string r, CancellationToken _) =>
            Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(r) ?? []);
        var service = new ModInstallService(
            new SettingsStore(_dir.Combine("settings.json")),
            new ArchiveDownloader(new HttpClient(_http), _dir.Combine("Archives")),
            new FakeVersionSource(GetVersions),
            (_, _) => Task.FromResult(false));
        var install = new GameInstall(_game.Root, GameLauncher.Epic, GameHost.CrossOver, GameKind.Client, null, "test",
            new GameBuild(513885, "1.2.4.1"));
        _resolution = new ModResolutionService(new FakeVersionSource(GetVersions), service);
        var store = new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        store.Save(new("Playthrough", []));
        store.SetActive(_game.Root, "Playthrough");
        _profiles = new GameProfiles(store, service);
        var card = new GameInstallViewModel(install, service, _resolution, _profiles, NullLogger<GameInstallViewModel>.Instance);
        await card.CheckAsync();
        return card;
    }

    [Fact]
    public async Task After_importing_offers_to_install_the_missing_mod_loader_and_installs_it()
    {
        Publish("SML", "3.12.0", serve: true);
        Publish("MapZoom", "2.1.0", serve: false, "SML"); // already in the game; never downloaded
        PlaceForeign("MapZoom", "2.1.0", "SML");
        var card = await CardAsync();

        await card.ImportToCurrentCommand.ExecuteAsync(null);

        Assert.True(card.HasFix);
        Assert.Equal("Install SML 3.12.0 (needed by MapZoom 2.1.0)", card.FixLines[0]);

        await card.FixCommand.ExecuteAsync(null);

        Assert.False(card.HasFix);
        Assert.Equal("Installed SML 3.12.0.", card.Notice);
        Assert.NotNull(_game.Read("Mods/SML"));
        Assert.Equal(["MapZoom", "SML"], new ModInstaller(_game.Root, () => false).Installed.Keys.Order());
    }

    [Fact]
    public async Task Explains_when_a_missing_mod_cannot_be_installed()
    {
        PlaceForeign("PrivateMod", "1.0.0", "SML"); // not on ficsit.app
        Publish("SML", "3.12.0", serve: false);
        var card = await CardAsync();

        await card.ImportToCurrentCommand.ExecuteAsync(null);

        Assert.False(card.HasFix);
        Assert.Contains("PrivateMod is not on ficsit.app", card.FixProblem);
    }

    [Fact]
    public async Task Nothing_is_offered_when_nothing_is_missing()
    {
        Publish("SML", "3.12.0", serve: false);
        PlaceForeign("SML", "3.12.0");
        var card = await CardAsync();

        await card.ImportToCurrentCommand.ExecuteAsync(null);

        Assert.False(card.HasFix);
        Assert.False(card.HasFixProblem);
    }

    [Fact]
    public async Task The_launch_check_finds_missing_mods_too()
    {
        Publish("SML", "3.12.0", serve: true);
        Publish("MapZoom", "2.1.0", serve: false, "SML");
        PlaceForeign("MapZoom", "2.1.0", "SML");
        await (await CardAsync()).ImportToCurrentCommand.ExecuteAsync(null);

        var next = await CardAsync(); // e.g. "Not now", then ADA restarted

        Assert.True(next.HasFix);
    }

    [Fact]
    public async Task Updates_are_found_never_installed_until_chosen_in_the_updates_window()
    {
        Publish("SML", "3.12.0", serve: false);
        PlaceForeign("SML", "3.12.0");
        var card = await CardAsync();
        await card.ImportToCurrentCommand.ExecuteAsync(null);
        Assert.False(card.HasUpdates);

        PublishWithChangelog("SML", "3.12.4", serve: false, "Fixed a crash.");
        PublishWithChangelog("SML", "3.12.5", serve: true, "**Faster** loading.");
        var store = new ModCatalogStore(_dir.Combine("catalog.db"));
        var catalog = new CatalogStatusViewModel(new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), store),
            NullLogger<CatalogStatusViewModel>.Instance);
        var dashboard = DashboardFactory.Create(_dir, new OneGame(card), _ => card, catalog, store,
            profiles: _profiles, resolution: _resolution);
        await dashboard.ScanCommand.ExecuteAsync(null);
        await dashboard.FindUpdatesAsync();

        // Found and flagged (the Dashboard arrow), but nothing installed.
        Assert.True(dashboard.HasUpdates);
        Assert.Equal("1 mod update available", card.UpdatesTitle);
        Assert.Equal("3.12.0", new ModInstaller(_game.Root, () => false).Installed["SML"].Version);

        var updates = new UpdatesViewModel(dashboard, _resolution!, _profiles!, store,
            new FakeVersionSource((r, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(r) ?? [])),
            NullLogger<UpdatesViewModel>.Instance);
        await updates.CheckAsync();

        var item = Assert.Single(updates.Items);
        Assert.Equal("3.12.0 → 3.12.5", item.VersionText);
        Assert.Equal("1 mod has an update.", updates.StatusText);

        await item.ToggleChangelogCommand.ExecuteAsync(null);
        Assert.Equal("3.12.5\nFaster loading.\n\n3.12.4\nFixed a crash.", item.Changelog);

        await item.UpdateCommand.ExecuteAsync(null);

        Assert.Equal("Updated SML 3.12.5.", updates.Notice);
        Assert.Empty(updates.Items);
        Assert.Equal("All installed mods are up to date.", updates.StatusText);
        Assert.Equal("3.12.5", new ModInstaller(_game.Root, () => false).Installed["SML"].Version);
    }

    private sealed class OneGame(GameInstallViewModel card) : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => [card.Install];
    }
}
