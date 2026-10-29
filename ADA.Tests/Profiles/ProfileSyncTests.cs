using System.Security.Cryptography;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;

namespace ADA.Tests.Profiles;

/// <summary>Only mods both sides of a multiplayer session need count towards being out of sync.</summary>
public sealed class ProfileSyncTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;
    private readonly ProfileStore _store;
    private readonly GameInstall _install;
    private readonly FakeHttpHandler _http = new();
    private readonly Dictionary<string, List<CatalogVersion>> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _lookedUp = [];
    private readonly ModInstallService _service;
    private bool _lookupsFail;

    public ProfileSyncTests()
    {
        _game = new(_dir.Combine("Game"));
        _store = new(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        _install = new(_game.Root, GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "Test", new(513885, "1.2"));
        var settings = new SettingsStore(_dir.Combine("settings.json"));
        settings.Save(new(DownloadRetries: 0));
        _service = new(settings, new(new HttpClient(_http), _dir.Combine("Archives")),
            new FakeVersionSource((reference, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(reference) ?? [])),
            (_, _) => Task.FromResult(false));
        _store.SetActive(_game.Root, "Playthrough");
    }

    public void Dispose() => _dir.Dispose();

    private ProfileSync Sync() => new(_store, _service, new FakeVersionSource((reference, _) =>
    {
        _lookedUp.Add(reference);
        return _lookupsFail
            ? throw new HttpRequestException("offline")
            : Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(reference) ?? []);
    }));

    [Fact]
    public async Task AClientOnlyModMissingHereDoesntCount()
    {
        await _service.InstallAsync(_install, [Publish("Storage", "1.0.0", required: true)]);
        Publish("BetterGrass", "2.0.0", required: false);
        _store.Save(new("Playthrough", [new("Storage", "1.0.0"), new("BetterGrass", "2.0.0")]));

        var state = await Sync().CompareAsync(_install);

        Assert.True(state.InSync);
        Assert.Equal("Playthrough", state.Profile);
    }

    [Fact]
    public async Task MissingExtraAndDifferentNeededModsAreListed()
    {
        await _service.InstallAsync(_install, [Publish("Storage", "1.0.0", required: true), Publish("Vehicles", "1.0.0", required: true)]);
        Publish("Storage", "1.1.0", required: true);
        Publish("Trains", "3.0.0", required: true);
        _store.Save(new("Playthrough", [new("Storage", "1.1.0"), new("Trains", "3.0.0")]));

        var state = await Sync().CompareAsync(_install);

        Assert.Equal(["Storage 1.1.0", "Trains 3.0.0"], state.Missing);
        Assert.Equal(["Vehicles"], state.Extra);
        Assert.Equal(3, state.Count);
    }

    [Fact]
    public async Task AVersionDifferenceCountsWhenOnlyTheInstalledVersionIsNeeded()
    {
        await _service.InstallAsync(_install, [Publish("Storage", "1.0.0", required: true)]);
        Publish("Storage", "0.9.0", required: false);
        _store.Save(new("Playthrough", [new("Storage", "0.9.0")]));

        Assert.Equal(["Storage 0.9.0"], (await Sync().CompareAsync(_install)).Missing);
    }

    [Fact]
    public async Task AFailedLookupCountsAndAMatchingProfileNeedsNoLookups()
    {
        await _service.InstallAsync(_install, [Publish("Storage", "1.0.0", required: false)]);
        _store.Save(new("Playthrough", [new("Storage", "1.0.0")]));

        Assert.True((await Sync().CompareAsync(_install)).InSync);
        Assert.Empty(_lookedUp);

        _store.Save(new("Playthrough", []));
        _lookupsFail = true;
        Assert.Equal(["Storage"], (await Sync().CompareAsync(_install)).Extra);
    }

    [Fact]
    public async Task NoProfileIsReportedAsNone()
    {
        _store.ClearActive(_game.Root);

        var state = await Sync().CompareAsync(_install);

        Assert.Null(state.Profile);
        Assert.True(state.InSync);
    }

    private CatalogVersion Publish(string reference, string version, bool required)
    {
        var path = new SmodBuilder(reference, version).Typical().WriteTo(_dir.Combine("Published", reference, version));
        var data = File.ReadAllBytes(path);
        var link = $"/{reference}/{version}";
        var item = CatalogFixtures.Version(reference, version) with
        {
            RequiredOnRemote = required,
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
