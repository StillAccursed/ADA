using System.Net;
using System.Security.Cryptography;
using Overlord.Core.Catalog;
using Overlord.Core.Games;
using Overlord.Core.Resolution;
using Overlord.Core.Settings;
using Overlord.Infrastructure.Downloads;
using Overlord.Infrastructure.Installing;
using Overlord.Infrastructure.Settings;
using Overlord.Tests.Fixtures;

namespace Overlord.Tests.Installing;

public sealed class ModInstallServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;
    private readonly GameInstall _install;
    private readonly FakeHttpHandler _http = new();
    private readonly SettingsStore _settings;
    private readonly string _archives;
    private readonly Dictionary<string, List<CatalogVersion>> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _files = [];
    private bool _gameRunning;

    public ModInstallServiceTests()
    {
        _game = new GameFolder(_dir.Combine("Satisfactory"));
        _install = new GameInstall(_game.Root, GameLauncher.Epic, GameHost.CrossOver, GameKind.Client, "513885.383", "test");
        _settings = new SettingsStore(_dir.Combine("Config", "settings.json"));
        _archives = _dir.Combine("Staging", "Archives");
    }

    public void Dispose() => _dir.Dispose();

    private ModInstallService Service() => new(
        _settings,
        new ArchiveDownloader(new HttpClient(_http), _archives, time: new InstantTimeProvider()),
        (reference, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(reference) ?? []),
        _ => Task.FromResult(_gameRunning));

    /// <summary>Publishes a mod version on the fake ficsit.app; returns the catalogue entry.</summary>
    private CatalogVersion Publish(string reference, string version, bool gameFeature = false,
        IReadOnlyList<VersionDependency>? deps = null)
    {
        var path = new SmodBuilder(reference, version).GameFeature(gameFeature).Typical().WriteTo(_dir.Combine("Published", version));
        var bytes = File.ReadAllBytes(path);
        var link = $"/v1/version/{reference}-{version}/Windows/download";
        _files[link] = bytes;
        var entry = new CatalogVersion($"{reference}-{version}", reference, version, ">=513885", VersionStability.Release, false,
            DateTimeOffset.UnixEpoch,
            [new VersionTarget(TargetPlatform.Windows, link, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))], deps ?? []);
        if (!_catalog.TryGetValue(reference, out var list))
            _catalog[reference] = list = [];
        list.Add(entry);
        return entry;
    }

    private void Serve(params CatalogVersion[] versions)
    {
        foreach (var v in versions)
            _http.RespondBytes(_files[v.Targets[0].DownloadPath], v.Targets[0].DownloadPath);
    }

    private string[] KeptArchives() =>
        Directory.Exists(_archives)
            ? Directory.EnumerateFiles(_archives, "*.smod", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>().Order().ToArray()
            : [];

    private ModResolutionService Resolver() => new(
        (reference, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(reference) ?? []),
        Service());

    [Fact]
    public async Task Resolves_dependencies_keeping_installed_versions_then_installs_them()
    {
        var game = _install with { Build = new GameBuild(513885, "1.2.4.1") };
        var sml = Publish("SML", "3.12.0");
        Serve(sml);
        await Service().InstallAsync(game, [sml]);

        Publish("SML", "3.12.5"); // newer, but the installed 3.12.0 still fits
        var weather = Publish("WeatherExtended", "1.1.0", gameFeature: true, deps: [new VersionDependency("SML", "^3.12.0", false)]);

        var resolved = await Resolver().ResolveAsync(game, [new ModRequirement("WeatherExtended")]);

        Assert.True(resolved.Succeeded, string.Join(" ", resolved.Problems));
        Assert.Equal(["SML 3.12.0", "WeatherExtended 1.1.0"], resolved.Mods.Select(m => $"{m.Reference} {m.Version.Version}"));

        Serve(weather);
        var result = await Service().InstallAsync(game, resolved.Mods.Select(m => m.Version).ToList());
        Assert.Contains(result.Installed, m => m.Reference == "WeatherExtended");
        Assert.NotNull(_game.Read("Mods/GameFeatures/WeatherExtended"));
    }

    [Fact]
    public async Task Resolving_without_a_known_game_build_explains_why()
    {
        var result = await Resolver().ResolveAsync(_install with { Build = null }, [new ModRequirement("SML")]);

        Assert.Contains("build number", Assert.Single(result.Problems));
    }

    private GameInstall KnownBuild => _install with { Build = new GameBuild(513885, "1.2.4.1") };

    private static VersionDependency Needs(string reference, string range = "^3.12.0") => new(reference, range, false);

    private string[] InstalledNow() => new ModInstaller(_game.Root, () => false).Installed.Keys.Order().ToArray();

    [Fact]
    public async Task Installing_a_mod_pulls_in_its_dependencies_and_records_only_what_was_asked_for()
    {
        var sml = Publish("SML", "3.12.0");
        var weather = Publish("WeatherExtended", "1.1.0", gameFeature: true, deps: [Needs("SML")]);
        Serve(sml, weather);

        var plan = await Resolver().PlanInstallAsync(KnownBuild, "WeatherExtended");

        Assert.True(plan.Succeeded, string.Join(" ", plan.Problems));
        Assert.Equal(["SML", "WeatherExtended"], plan.Install.Select(i => i.Reference));
        Assert.Empty(plan.Uninstall);
        Assert.Equal(sml.Targets[0].Size + weather.Targets[0].Size, plan.DownloadBytes);

        await Resolver().ApplyAsync(KnownBuild, plan);

        Assert.Equal(["SML", "WeatherExtended"], InstalledNow());
        Assert.Equal(["WeatherExtended"], new ModInstaller(_game.Root, () => false).Requested);
    }

    [Fact]
    public async Task Uninstalling_removes_dependencies_nothing_else_needs()
    {
        Serve(Publish("SML", "3.12.0"), Publish("A", "1.0.0", deps: [Needs("SML")]), Publish("B", "1.0.0", deps: [Needs("SML")]));
        await Resolver().ApplyAsync(KnownBuild, await Resolver().PlanInstallAsync(KnownBuild, "A"));
        await Resolver().ApplyAsync(KnownBuild, await Resolver().PlanInstallAsync(KnownBuild, "B"));

        var removeA = await Resolver().PlanUninstallAsync(KnownBuild, "A");
        Assert.Equal(["A"], removeA.Uninstall); // B still needs SML
        await Resolver().ApplyAsync(KnownBuild, removeA);

        var removeB = await Resolver().PlanUninstallAsync(KnownBuild, "B");
        Assert.Equal(["B", "SML"], removeB.Uninstall);
        await Resolver().ApplyAsync(KnownBuild, removeB);

        Assert.Empty(InstalledNow());
        Assert.Null(_game.Read("Mods/SML"));
    }

    [Fact]
    public async Task Refuses_to_uninstall_a_mod_another_one_needs()
    {
        Serve(Publish("SML", "3.12.0"), Publish("A", "1.0.0", deps: [Needs("SML")]));
        await Resolver().ApplyAsync(KnownBuild, await Resolver().PlanInstallAsync(KnownBuild, "A"));

        var plan = await Resolver().PlanUninstallAsync(KnownBuild, "SML");

        Assert.Equal("SML can't be removed while A 1.0.0 needs it.", Assert.Single(plan.Problems));
    }

    [Fact]
    public async Task Mods_installed_before_requests_were_recorded_are_never_removed_as_unneeded()
    {
        var sml = Publish("SML", "3.12.0");
        Serve(sml, Publish("A", "1.0.0", deps: [Needs("SML")]));
        await Service().InstallAsync(KnownBuild, [sml]); // e.g. a repair: no record of what was asked for

        var plan = await Resolver().PlanInstallAsync(KnownBuild, "A");
        await Resolver().ApplyAsync(KnownBuild, plan);
        await Resolver().ApplyAsync(KnownBuild, await Resolver().PlanUninstallAsync(KnownBuild, "A"));

        Assert.Equal(["SML"], InstalledNow());
    }

    [Fact]
    public async Task Asking_for_a_dependency_keeps_it_once_nothing_needs_it()
    {
        Serve(Publish("SML", "3.12.0"), Publish("A", "1.0.0", deps: [Needs("SML")]));
        await Resolver().ApplyAsync(KnownBuild, await Resolver().PlanInstallAsync(KnownBuild, "A"));

        var keepSml = await Resolver().PlanInstallAsync(KnownBuild, "SML");
        Assert.False(keepSml.ChangesNothing);
        await Resolver().ApplyAsync(KnownBuild, keepSml);
        await Resolver().ApplyAsync(KnownBuild, await Resolver().PlanUninstallAsync(KnownBuild, "A"));

        Assert.Equal(["SML"], InstalledNow());
    }

    [Fact]
    public async Task Plans_refuse_to_overwrite_mods_another_manager_installed()
    {
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: [Needs("SML")]);
        Directory.CreateDirectory(_game.ModFolder("Mods/SML"));

        var plan = await Resolver().PlanInstallAsync(KnownBuild, "A");

        Assert.StartsWith("SML is already in the Mods folder, but not installed by Overlord", Assert.Single(plan.Problems));
    }

    [Fact]
    public async Task Update_plans_move_installed_mods_to_the_newest_versions_that_fit()
    {
        Serve(Publish("SML", "3.12.0"), Publish("A", "1.0.0", deps: [Needs("SML")]));
        await Resolver().ApplyAsync(KnownBuild, await Resolver().PlanInstallAsync(KnownBuild, "A"));
        Serve(Publish("SML", "3.12.5"));
        Publish("A", "1.1.0", deps: [Needs("SML", "^3.13.0")]); // needs an SML this game can't have

        var plan = await Resolver().PlanUpdateAsync(KnownBuild);

        Assert.Equal(["Update SML 3.12.0 → 3.12.5"], plan.Install.Select(i => $"Update {i.Reference} {i.InstalledVersion} → {i.Version}"));
        Assert.Empty(plan.Uninstall);
        await Resolver().ApplyAsync(KnownBuild, plan);
        Assert.True((await Resolver().PlanUpdateAsync(KnownBuild)).ChangesNothing);
    }

    [Fact]
    public async Task Updating_one_mod_leaves_the_others_as_they_are()
    {
        Serve(Publish("SML", "3.12.0"), Publish("A", "1.0.0", deps: [Needs("SML")]));
        await Resolver().ApplyAsync(KnownBuild, await Resolver().PlanInstallAsync(KnownBuild, "A"));
        Publish("SML", "3.12.5");
        Serve(Publish("A", "1.1.0", deps: [Needs("SML")]));

        var plan = await Resolver().PlanUpdateAsync(KnownBuild, only: "A");

        Assert.Equal(["A 1.1.0"], plan.Install.Select(i => $"{i.Reference} {i.Version}"));
    }

    [Fact]
    public async Task Update_plans_never_go_back_from_an_installed_beta_to_an_older_release()
    {
        Serve(Publish("SML", "3.12.0"));
        var beta = Publish("SML", "3.13.0-beta.1") with { Stability = VersionStability.Beta };
        _catalog["SML"][1] = beta;
        Serve(beta);
        await Service().InstallAsync(KnownBuild, [beta]);

        Assert.True((await Resolver().PlanUpdateAsync(KnownBuild)).ChangesNothing);
    }

    [Fact]
    public async Task Installing_what_is_already_installed_changes_nothing()
    {
        Serve(Publish("SML", "3.12.0"));
        await Resolver().ApplyAsync(KnownBuild, await Resolver().PlanInstallAsync(KnownBuild, "SML"));

        Assert.True((await Resolver().PlanInstallAsync(KnownBuild, "SML")).ChangesNothing);
    }

    [Fact]
    public async Task Downloads_then_installs_and_keeps_archives_by_default()
    {
        var sml = Publish("SML", "3.12.0");
        var weather = Publish("WeatherExtended", "1.1.0", gameFeature: true);
        Serve(sml, weather);

        var result = await Service().InstallAsync(_install, [sml, weather]);

        Assert.Equal(["SML", "WeatherExtended"], result.Installed.Select(m => m.Reference));
        Assert.NotNull(_game.Read("Mods/SML"));
        Assert.NotNull(_game.Read("Mods/GameFeatures/WeatherExtended"));
        Assert.Equal(["1.1.0-Windows.smod", "3.12.0-Windows.smod"], KeptArchives());
    }

    [Fact]
    public async Task Deletes_archives_after_install_when_the_setting_is_off()
    {
        _settings.Save(new AppSettings(KeepDownloadedArchives: false));
        var sml = Publish("SML", "3.12.0");
        Serve(sml);

        await Service().InstallAsync(_install, [sml]);

        Assert.NotNull(_game.Read("Mods/SML"));
        Assert.Empty(KeptArchives());
        Assert.False(Directory.Exists(Path.Combine(_archives, "SML")));
    }

    [Fact]
    public async Task Uses_the_retry_setting_for_downloads()
    {
        _settings.Save(new AppSettings(DownloadRetries: 2));
        var sml = Publish("SML", "3.12.0");
        _http.Fail(HttpStatusCode.BadGateway).Fail(HttpStatusCode.BadGateway).RespondBytes(_files[sml.Targets[0].DownloadPath]);

        await Service().InstallAsync(_install, [sml]);
        Assert.NotNull(_game.Read("Mods/SML"));

        _settings.Save(new AppSettings(DownloadRetries: 0));
        var other = Publish("Other", "1.0.0");
        _http.Fail(HttpStatusCode.BadGateway).RespondBytes(_files[other.Targets[0].DownloadPath]);
        await Assert.ThrowsAsync<ArchiveDownloadException>(() => Service().InstallAsync(_install, [other]));
    }

    [Fact]
    public async Task A_failed_download_installs_nothing()
    {
        var sml = Publish("SML", "3.12.0");
        var weather = Publish("WeatherExtended", "1.1.0");
        _settings.Save(new AppSettings(DownloadRetries: 0));
        _http.RespondBytes(_files[sml.Targets[0].DownloadPath], sml.Targets[0].DownloadPath)
            .Fail(HttpStatusCode.NotFound, weather.Targets[0].DownloadPath);

        await Assert.ThrowsAsync<ArchiveDownloadException>(() => Service().InstallAsync(_install, [sml, weather]));

        Assert.Null(_game.Read("Mods/SML"));
        Assert.Empty(Service().InstallerFor(_install).Installed);
    }

    [Fact]
    public async Task Refuses_before_downloading_when_the_game_is_running()
    {
        _gameRunning = true;
        var sml = Publish("SML", "3.12.0");

        await Assert.ThrowsAsync<GameRunningException>(() => Service().InstallAsync(_install, [sml]));
        Assert.Empty(_http.RequestBodies);
    }

    [Fact]
    public async Task Repairs_from_the_kept_archive_without_downloading()
    {
        var sml = Publish("SML", "3.12.0");
        Serve(sml);
        await Service().InstallAsync(_install, [sml]);
        File.Delete(Path.Combine(_game.Mods, "SML", "Resources", "Icon128.png"));
        var requestsBefore = _http.RequestBodies.Count;

        var status = await Service().RecoverAndCheckAsync(_install);
        var repair = await Service().RepairAsync(_install, status.Check);

        Assert.Equal(["SML"], repair.Repaired);
        Assert.Empty(repair.Failed);
        Assert.Equal(requestsBefore, _http.RequestBodies.Count);
        Assert.Empty((await Service().RecoverAndCheckAsync(_install)).Check.NeedingRepair);
    }

    [Fact]
    public async Task Repair_downloads_again_when_archives_are_not_kept()
    {
        _settings.Save(new AppSettings(KeepDownloadedArchives: false));
        var sml = Publish("SML", "3.12.0");
        Serve(sml);
        await Service().InstallAsync(_install, [sml]);
        Directory.Delete(Path.Combine(_game.Mods, "SML"), recursive: true);
        Serve(sml);

        var repair = await Service().RepairAsync(_install, (await Service().RecoverAndCheckAsync(_install)).Check);

        Assert.Equal(["SML"], repair.Repaired);
        Assert.NotNull(_game.Read("Mods/SML"));
    }

    [Fact]
    public async Task Repair_reports_versions_that_ficsit_app_no_longer_lists()
    {
        var sml = Publish("SML", "3.12.0");
        Serve(sml);
        await Service().InstallAsync(_install, [sml]);
        Directory.Delete(Path.Combine(_game.Mods, "SML"), recursive: true);
        _catalog.Clear();

        var repair = await Service().RepairAsync(_install, (await Service().RecoverAndCheckAsync(_install)).Check);

        Assert.Empty(repair.Repaired);
        Assert.Contains("no longer listed", repair.Failed["SML"]);
    }

    [Fact]
    public async Task Launch_check_on_an_untouched_game_creates_nothing()
    {
        var status = await Service().RecoverAndCheckAsync(_install);

        Assert.False(status.Recovery.HadInterruptedTransaction);
        Assert.Empty(status.Check.Mods);
        Assert.False(Directory.Exists(_game.Work));
    }

    [Fact]
    public async Task Launch_recovery_waits_while_the_game_is_running()
    {
        // Leave an interrupted transaction behind.
        var archive = new SmodBuilder("A", "1.0.0").Typical().WriteTo(_dir.Combine("Local"));
        var crashing = new ModInstaller(_game.Root, () => false, cp =>
        {
            if (cp == "ready:A")
                throw new SimulatedCrashException(cp);
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply([new ModChange.Install(archive, TargetPlatform.Windows)]));

        _gameRunning = true;
        var whileRunning = await Service().RecoverAndCheckAsync(_install);
        Assert.True(whileRunning.RecoveryDeferred);
        Assert.True(Service().InstallerFor(_install).HasInterruptedTransaction);

        _gameRunning = false;
        var afterwards = await Service().RecoverAndCheckAsync(_install);
        Assert.False(afterwards.RecoveryDeferred);
        Assert.Equal(["A"], afterwards.Recovery.RolledForward);
        Assert.NotNull(_game.Read("Mods/A"));
    }

    [Theory]
    [InlineData(GameKind.Client, GameHost.CrossOver, TargetPlatform.Windows)]
    [InlineData(GameKind.Client, GameHost.LinuxCompat, TargetPlatform.Windows)]
    [InlineData(GameKind.DedicatedServer, GameHost.Windows, TargetPlatform.WindowsServer)]
    [InlineData(GameKind.DedicatedServer, GameHost.Linux, TargetPlatform.LinuxServer)]
    public void Picks_the_build_for_the_game(GameKind kind, GameHost host, TargetPlatform expected) =>
        Assert.Equal(expected, ModInstallService.TargetFor(_install with { Kind = kind, Host = host }));

    [Fact]
    public async Task Refuses_before_downloading_when_the_archives_drive_is_too_full()
    {
        var huge = Publish("Huge", "1.0.0");
        huge = huge with { Targets = [huge.Targets[0] with { Size = long.MaxValue / 4 }] };

        var e = await Assert.ThrowsAsync<InstallException>(() => Service().InstallAsync(_install, [huge]));

        Assert.Contains("Not enough free space", e.Message);
        Assert.Empty(_http.RequestBodies);
    }
}
