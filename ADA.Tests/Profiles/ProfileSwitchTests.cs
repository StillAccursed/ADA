using System.Security.Cryptography;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Profiles;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;

namespace ADA.Tests.Profiles;

public sealed class ProfileSwitchTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;
    private readonly ProfileStore _store;
    private readonly GameInstall _install;
    private readonly FakeHttpHandler _http = new();
    private readonly Dictionary<string, List<CatalogVersion>> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModInstallService _service;
    private readonly GameProfiles _profiles;
    private readonly ModResolutionService _resolution;
    private bool _running;

    public ProfileSwitchTests()
    {
        _game = new(_dir.Combine("Game"));
        _store = new(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        _install = new(_game.Root, GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "Test", new(513885, "1.2"));
        var settings = new SettingsStore(_dir.Combine("settings.json"));
        settings.Save(new(DownloadRetries: 0));
        _service = new(settings, new(new HttpClient(_http), _dir.Combine("Archives")),
            new FakeVersionSource((reference, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(reference) ?? [])),
            _ => Task.FromResult(_running));
        _profiles = new(_store, _service);
        _resolution = new(new FakeVersionSource((reference, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(reference) ?? [])), _service);
        _store.SetActive(_game.Root, "Current");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Applies_exact_versions_and_preserves_the_outgoing_saved_profile()
    {
        var old = Publish("A", "1.0.0");
        Publish("A", "2.0.0");
        await _service.InstallAsync(_install, [old]);
        await _profiles.RecordInstalledAsync(_install);
        var target = new ModProfile("Target", [new("A", "2.0.0")]);
        _store.Save(target);

        await _profiles.SwitchAsync(_install, target, _resolution);

        Assert.Equal("Target", _store.ActiveName(_game.Root));
        Assert.Equal("2.0.0", Installer().Installed["A"].Version);
        Assert.Equal("1.0.0", Assert.Single(_store.Get("Current")!.Mods).Version);
        Assert.False(Installer().HasPendingProfileActivation);
        Assert.Equal(["A"], Installer().Requested);
    }

    [Fact]
    public async Task An_empty_profile_removes_managed_mods_and_leaves_foreign_files_alone()
    {
        await _service.InstallAsync(_install, [Publish("A", "1.0.0")]);
        await _profiles.RecordInstalledAsync(_install);
        Directory.CreateDirectory(_game.ModFolder("Mods/Foreign"));
        File.WriteAllText(_game.ModFolder("Mods/Foreign/readme.txt"), "Keep me");

        await _profiles.SwitchAsync(_install, new("Empty", []), _resolution);

        Assert.Empty(Installer().Installed);
        Assert.Equal("Keep me", File.ReadAllText(_game.ModFolder("Mods/Foreign/readme.txt")));
        Assert.Equal("Empty", _store.ActiveName(_game.Root));
        Assert.Equal("1.0.0", Assert.Single(_store.Get("Current")!.Mods).Version);
    }

    [Fact]
    public async Task Switching_without_a_profile_refuses_to_discard_the_installed_mods()
    {
        var store = new ProfileStore(_dir.Combine("Fresh"), _dir.Combine("fresh.json"));
        var profiles = new GameProfiles(store, _service);
        await _service.InstallAsync(_install, [Publish("A", "1.0.0")]);

        var error = await Assert.ThrowsAsync<InstallException>(() => profiles.SwitchAsync(_install, new("Empty", []), _resolution));

        Assert.Contains("Create a profile", error.Message);
        Assert.Equal("1.0.0", Installer().Installed["A"].Version);
        Assert.Null(store.ActiveName(_game.Root));
    }

    [Fact]
    public async Task Creating_a_profile_saves_the_installed_mods_and_activates_it()
    {
        var store = new ProfileStore(_dir.Combine("Fresh"), _dir.Combine("fresh.json"));
        var profiles = new GameProfiles(store, _service);
        await _service.InstallAsync(_install, [Publish("A", "1.0.0")]);

        await profiles.CreateAsync(_install, "  Playthrough 1 ");

        Assert.Equal("Playthrough 1", store.ActiveName(_game.Root));
        Assert.Equal("1.0.0", Assert.Single(store.Get("Playthrough 1")!.Mods).Version);
        await Assert.ThrowsAsync<InvalidProfileException>(() => profiles.CreateAsync(_install, "playthrough 1"));
    }

    [Fact]
    public async Task Missing_exact_version_does_not_choose_a_newer_version_or_change_the_active_profile()
    {
        await _service.InstallAsync(_install, [Publish("A", "1.0.0")]);
        Publish("A", "3.0.0");

        await Assert.ThrowsAsync<InstallException>(() => _profiles.SwitchAsync(_install, new("Missing", [new("A", "2.0.0")]), _resolution));

        Assert.Equal("Current", _store.ActiveName(_game.Root));
        Assert.Equal("1.0.0", Installer().Installed["A"].Version);
        Assert.False(Installer().HasPendingProfileActivation);
    }

    [Fact]
    public async Task Failed_download_keeps_old_files_and_profile()
    {
        await _service.InstallAsync(_install, [Publish("A", "1.0.0")]);
        var version = CatalogFixtures.Version("A", "2.0.0");
        _catalog["A"].Add(version);
        _http.Fail(System.Net.HttpStatusCode.NotFound);

        await Assert.ThrowsAnyAsync<Exception>(() => _profiles.SwitchAsync(_install, new("Offline", [new("A", "2.0.0")]), _resolution));

        Assert.Equal("Current", _store.ActiveName(_game.Root));
        Assert.Equal("1.0.0", Installer().Installed["A"].Version);
    }

    [Fact]
    public async Task Running_game_and_concurrent_operations_block_switching()
    {
        _running = true;
        await Assert.ThrowsAsync<GameRunningException>(() => _profiles.SwitchAsync(_install, new("Empty", []), _resolution));
        _running = false;
        using var operation = _service.BeginOperation(_install);
        await Assert.ThrowsAsync<InstallationBusyException>(() => _profiles.SwitchAsync(_install, new("Empty", []), _resolution));
        Assert.Equal("Current", _store.ActiveName(_game.Root));
    }

    [Fact]
    public async Task Foreign_collision_is_refused_before_downloading()
    {
        Publish("A", "1.0.0");
        Directory.CreateDirectory(_game.ModFolder("Mods/A"));
        var remaining = _http.Remaining;

        await Assert.ThrowsAsync<InstallException>(() => _profiles.SwitchAsync(_install, new("Target", [new("A", "1.0.0")]), _resolution));

        Assert.Equal(remaining, _http.Remaining);
        Assert.Equal("Current", _store.ActiveName(_game.Root));
    }

    [Fact]
    public async Task Incompatible_saved_versions_do_not_replace_working_installed_versions()
    {
        await _service.InstallAsync(_install, [Publish("A", "1.0.0")]);
        var incompatible = Publish("A", "2.0.0") with { GameVersionRange = ">=999999" };
        _catalog["A"][1] = incompatible;
        var downloads = _http.Remaining;

        await Assert.ThrowsAsync<InstallException>(() => _profiles.SwitchAsync(_install, new("Target", [new("A", "2.0.0")]), _resolution));

        Assert.Equal(downloads, _http.Remaining);
        Assert.Equal("1.0.0", Installer().Installed["A"].Version);
        Assert.Equal("Current", _store.ActiveName(_game.Root));
    }

    [Fact]
    public void Crash_after_removing_one_mod_rolls_back_the_whole_profile_switch()
    {
        var old = Archive("1.0.0");
        var next = Archive("2.0.0");
        var other = new SmodBuilder("B", "1.0.0").Typical().WriteTo(_dir.Combine("Published", "B"));
        Installer().Apply([new ModChange.Install(old, TargetPlatform.Windows), new ModChange.Install(other, TargetPlatform.Windows)]);
        Assert.Throws<SimulatedCrashException>(() => Installer(point =>
        {
            if (point == "done:B")
            {
                throw new SimulatedCrashException(point);
            }
        }).ApplyProfile([new ModChange.Uninstall("B"), new ModChange.Install(next, TargetPlatform.Windows)], new("Target", ["A"])));

        Installer().Recover();

        Assert.Equal(["A", "B"], Installer().Installed.Keys.Order());
        Assert.All(Installer().Installed.Values, mod => Assert.Equal("1.0.0", mod.Version));
        Assert.Equal(GameFolder.Expected(other), _game.Read("Mods/B"));
        Assert.Empty(Installer().Check(full: true).NeedingRepair);
        Assert.False(Installer().HasPendingProfileActivation);
    }

    [Fact]
    public async Task A_committed_journal_without_an_activation_marker_cannot_overwrite_the_old_profile()
    {
        Installer().Apply([new ModChange.Install(Archive("1.0.0"), TargetPlatform.Windows)]);
        _store.Save(new("Current", [new("A", "1.0.0")]));
        var next = Archive("2.0.0");
        Assert.Throws<SimulatedCrashException>(() => Installer(point =>
        {
            if (point == "transaction-committed")
            {
                throw new SimulatedCrashException(point);
            }
        }).ApplyProfile([new ModChange.Install(next, TargetPlatform.Windows)], new("Target", ["A"])));

        await Assert.ThrowsAsync<InstallException>(() => _profiles.RecordInstalledAsync(_install));
        Assert.Equal("1.0.0", Assert.Single(_store.Get("Current")!.Mods).Version);
        Installer().Recover();
        await _profiles.RecordInstalledAsync(_install);
        Assert.Equal("Target", _store.ActiveName(_game.Root));
        Assert.Equal("1.0.0", Assert.Single(_store.Get("Current")!.Mods).Version);
    }

    [Theory]
    [InlineData("journal-created", false)]
    [InlineData("ready:A", false)]
    [InlineData("done:A", false)]
    [InlineData("transaction-committed", true)]
    [InlineData("profile-activation-pending", true)]
    [InlineData("work-cleaned", true)]
    [InlineData("journal-removed", true)]
    public async Task Crash_recovery_keeps_profile_and_files_in_step(string checkpoint, bool committed)
    {
        var old = Archive("1.0.0");
        var next = Archive("2.0.0");
        Installer().Apply([new ModChange.Install(old, TargetPlatform.Windows)]);
        _store.Save(new("Current", [new("A", "1.0.0")]));
        _store.Save(new("Target", [new("A", "2.0.0")]));
        Assert.Throws<SimulatedCrashException>(() => Installer(point =>
        {
            if (point == checkpoint)
            {
                throw new SimulatedCrashException(point);
            }
        }).ApplyProfile([new ModChange.Install(next, TargetPlatform.Windows)], new("Target", ["A"])));

        Installer().Recover();
        await _profiles.RecordInstalledAsync(_install);

        Assert.Equal(committed ? "Target" : "Current", _store.ActiveName(_game.Root));
        Assert.Equal(committed ? "2.0.0" : "1.0.0", Installer().Installed["A"].Version);
        Assert.Equal("1.0.0", Assert.Single(_store.Get("Current")!.Mods).Version);
        Assert.False(Installer().HasPendingProfileActivation);
    }

    [Fact]
    public void Activation_failure_retains_marker_and_blocks_another_transaction_until_retry()
    {
        Installer().ApplyProfile([], new("Target", []));
        Assert.Throws<IOException>(() => Installer().CompleteProfileActivation(_ => throw new IOException("State unavailable")));
        Assert.True(Installer().HasPendingProfileActivation);
        Assert.Throws<InstallException>(() => Installer().Apply([]));

        Installer().CompleteProfileActivation(name => _store.SetActive(_game.Root, name));

        Assert.False(Installer().HasPendingProfileActivation);
        Assert.Equal("Target", _store.ActiveName(_game.Root));
    }

    [Fact]
    public void Crash_after_metadata_write_retries_activation_idempotently()
    {
        Installer().ApplyProfile([], new("Target", []));
        Assert.Throws<SimulatedCrashException>(() => Installer(point =>
        {
            if (point == "profile-activated")
            {
                throw new SimulatedCrashException(point);
            }
        }).CompleteProfileActivation(name => _store.SetActive(_game.Root, name)));

        Installer().CompleteProfileActivation(name => _store.SetActive(_game.Root, name));
        Assert.Equal("Target", _store.ActiveName(_game.Root));
        Assert.False(Installer().HasPendingProfileActivation);
    }

    private ModInstaller Installer(Action<string>? checkpoint = null) => new(_game.Root, () => _running, checkpoint);

    private string Archive(string version) => new SmodBuilder("A", version).Typical().WriteTo(_dir.Combine("Published", version));

    private CatalogVersion Publish(string reference, string version)
    {
        var path = new SmodBuilder(reference, version).Typical().WriteTo(_dir.Combine("Published", reference, version));
        var data = File.ReadAllBytes(path);
        var link = $"/{reference}/{version}";
        var item = CatalogFixtures.Version(reference, version) with
        {
            Targets = [new(TargetPlatform.Windows, link, data.Length, Convert.ToHexStringLower(SHA256.HashData(data)))],
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
}
