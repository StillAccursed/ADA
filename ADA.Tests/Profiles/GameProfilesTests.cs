using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;

namespace ADA.Tests.Profiles;

/// <summary>Mods another manager (e.g. SMM) installed: imported where they are, or saved as a profile.</summary>
public sealed class GameProfilesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;
    private readonly GameInstall _install;
    private readonly ProfileStore _store;

    public GameProfilesTests()
    {
        _game = new GameFolder(_dir.Combine("Satisfactory"));
        _install = new GameInstall(_game.Root, GameLauncher.Epic, GameHost.CrossOver, GameKind.Client, null, "test");
        _store = new ProfileStore(_dir.Combine("Config", "Profiles"), _dir.Combine("Config", "profiles.json"));
    }

    public void Dispose() => _dir.Dispose();

    private ModInstallService Installs() => new(
        new SettingsStore(_dir.Combine("settings.json")),
        new ArchiveDownloader(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
        (_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([]),
        _ => Task.FromResult(false));

    private GameProfiles Profiles() => new(_store, Installs());

    /// <summary>A mod folder as another manager leaves it.</summary>
    private void PlaceForeign(string location, string version = "1.0.0", params string[] requires)
    {
        var reference = location[(location.LastIndexOf('/') + 1)..];
        var folder = _game.ModFolder(location);
        Directory.CreateDirectory(Path.Combine(folder, "Binaries"));
        var plugins = string.Join(",", requires.Select(r => $$"""{ "Name": "{{r}}", "SemVersion": "^1.0.0" }"""));
        File.WriteAllText(Path.Combine(folder, reference + ".uplugin"), $$"""{ "SemVersion": "{{version}}", "FriendlyName": "{{reference}} mod", "Plugins": [{{plugins}}] }""");
        File.WriteAllText(Path.Combine(folder, "Binaries", "mod.dll"), "binary " + reference);
    }

    private async Task<IReadOnlyList<FoundMod>> Found() => (await Profiles().FindUnmanagedAsync(_install)).Mods;

    private ModInstaller Installer() => new(_game.Root, () => false);

    [Fact]
    public async Task Finds_mods_by_their_uplugin_and_skips_folders_that_are_not_mods()
    {
        PlaceForeign("Mods/SML", "3.12.0");
        PlaceForeign("Mods/GameFeatures/MapZoom", "2.1.0", "SML");
        Directory.CreateDirectory(_game.ModFolder("Mods/Junk"));

        var found = await Profiles().FindUnmanagedAsync(_install);

        Assert.Equal(["MapZoom 2.1.0 Mods/GameFeatures/MapZoom", "SML 3.12.0 Mods/SML"],
            found.Mods.Select(m => $"{m.Reference} {m.Version} {m.Location}").Order());
        Assert.Equal(["SML"], found.Mods.Single(m => m.Reference == "MapZoom").Dependencies);
        Assert.Contains("Mods/Junk", found.Unreadable.Keys);
    }

    [Fact]
    public async Task Importing_into_the_current_profile_takes_mods_over_without_touching_their_files()
    {
        PlaceForeign("Mods/SML", "3.12.0");
        PlaceForeign("Mods/GameFeatures/MapZoom", "2.1.0", "SML");
        var dll = _game.ModFolder("Mods/GameFeatures/MapZoom/Binaries/mod.dll");
        var writtenAt = File.GetLastWriteTimeUtc(dll);

        var result = await Profiles().ImportToCurrentAsync(_install, await Found());

        Assert.Equal(["MapZoom", "SML"], result.Adopted.Select(m => m.Reference).Order());
        Assert.Equal("binary MapZoom", File.ReadAllText(dll));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(dll));
        Assert.All(Installer().Check().Mods, m => Assert.Equal(ModHealth.Ok, m.Health));
        Assert.Empty(Installer().Check().Unmanaged);

        // SML is there because MapZoom needs it, so it goes once nothing does.
        Assert.Equal(["MapZoom"], Installer().Requested);
        Assert.Equal(ProfileStore.DefaultName, result.Profile.Name);
        Assert.Equal(["MapZoom 2.1.0", "SML 3.12.0"], _store.Get("Default")!.Mods.Select(m => $"{m.Name} {m.Version}"));
        Assert.Empty(await Found());
    }

    [Fact]
    public async Task Importing_into_a_new_profile_and_switching_makes_it_the_games_profile()
    {
        PlaceForeign("Mods/SML", "3.12.0");

        await Profiles().ImportToNewAsync(_install, await Found(), "  From SMM ", switchTo: true);

        Assert.Equal("From SMM", _store.ActiveName(_game.Root));
        Assert.Equal(["SML"], _store.Get("from smm")!.Mods.Select(m => m.Name));
        Assert.Single(Installer().Installed);
    }

    [Fact]
    public async Task Importing_into_a_new_profile_without_switching_leaves_the_mods_alone_and_stops_asking()
    {
        PlaceForeign("Mods/SML", "3.12.0");

        var result = await Profiles().ImportToNewAsync(_install, await Found(), "Later", switchTo: false);

        Assert.Empty(result.Adopted);
        Assert.Empty(Installer().Installed);
        Assert.Equal(ProfileStore.DefaultName, _store.ActiveName(_game.Root));
        Assert.Equal(["SML"], _store.Get("Later")!.Mods.Select(m => m.Name));
        Assert.Empty(await Found());
        Assert.NotNull(_game.Read("Mods/SML"));
    }

    [Fact]
    public async Task New_profile_names_must_be_unused()
    {
        _store.Save(new("Atria", []));
        PlaceForeign("Mods/SML", "3.12.0");

        await Assert.ThrowsAsync<InvalidProfileException>(() => Profiles().ImportToNewAsync(_install, [], "atria", switchTo: true));
        Assert.Equal("Give the profile a name.", Profiles().ProblemWithNewName(" "));
    }

    [Theory]
    [InlineData("Atria", "Atria.olp")]
    [InlineData("a/b:c", "a_b_c.olp")]
    [InlineData("CON", "_CON.olp")]
    [InlineData("..hidden", "_hidden.olp")]
    public void Profile_file_names_are_safe_on_every_OS(string name, string file)
    {
        _store.Save(new(name, []));

        Assert.Equal([file], Directory.EnumerateFiles(_store.Folder).Select(Path.GetFileName));
        Assert.Equal(name, _store.Get(name)!.Name);
    }
}
