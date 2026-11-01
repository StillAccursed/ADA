using ADA.Core.Activity;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Activity;
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
        new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])),
        (_, _) => Task.FromResult(false));

    private GameProfiles Profiles(ActivityLog? activity = null) => new(_store, Installs(), activity: activity);

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
    public async Task FindsModsByTheirUpluginAndSkipsFoldersThatAreNotMods()
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

    private void PlaceLibrary(string location, string relativePath)
    {
        var path = Path.Combine(_game.ModFolder(location), "Binaries", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "library");
    }

    [Fact]
    public async Task AClientBuildOnAServerIsRefusedAndSaysWhy()
    {
        var server = _install with { Kind = GameKind.DedicatedServer, Host = GameHost.Linux };
        PlaceForeign("Mods/BetterGrass");
        PlaceLibrary("Mods/BetterGrass", "Win64/FactoryGameSteam-BetterGrass-Win64-Shipping.dll");
        PlaceForeign("Mods/Storage");
        PlaceLibrary("Mods/Storage", "Linux/libFactoryServer-Storage-Linux-Shipping.so");
        PlaceForeign("Mods/Blueprints"); // content only: no libraries

        var found = await Profiles().FindUnmanagedAsync(server);

        Assert.Equal(["Blueprints", "Storage"], found.Mods.Select(m => m.Reference).Order());
        Assert.StartsWith("Client build, won't run on this server", Assert.Single(found.WrongBuild, w => w.Key == "Mods/BetterGrass").Value);
    }

    [Fact]
    public async Task AServerBuildInTheGameIsRefused()
    {
        PlaceForeign("Mods/Storage");
        PlaceLibrary("Mods/Storage", "Win64/FactoryServer-Storage-Win64-Shipping.dll");
        PlaceForeign("Mods/SML");
        PlaceLibrary("Mods/SML", "Win64/FactoryGameEGS-SML-Win64-Shipping.dll");
        PlaceLibrary("Mods/SML", "Win64/FactoryGameEGS-SML-Win64-Shipping.pdb");

        var found = await Profiles().FindUnmanagedAsync(_install);

        Assert.Equal(["SML"], found.Mods.Select(m => m.Reference));
        Assert.StartsWith("Server build, won't run in the game", Assert.Single(found.WrongBuild).Value);
    }

    [Theory]
    [InlineData("FactoryGameSteam-SML-Win64-Shipping.dll", TargetPlatform.Windows, true)]
    [InlineData("FactoryGameEGS-SML-Win64-Shipping.dll", TargetPlatform.Windows, true)]
    [InlineData("FactoryGameSteam-SML-Win64-Shipping.dll", TargetPlatform.WindowsServer, false)]
    [InlineData("FactoryServer-SML-Win64-Shipping.dll", TargetPlatform.WindowsServer, true)]
    [InlineData("FactoryServer-SML-Win64-Shipping.dll", TargetPlatform.Windows, false)]
    [InlineData("libFactoryServer-SML-Linux-Shipping.so", TargetPlatform.LinuxServer, true)]
    [InlineData("libFactoryServer-SML-Linux-Shipping.so", TargetPlatform.WindowsServer, false)]
    [InlineData("FactoryServer-SML-Win64-Shipping.dll", TargetPlatform.LinuxServer, false)]
    public void LibrariesAreMatchedToTheirPlatformByName(string fileName, TargetPlatform platform, bool expected) =>
        Assert.Equal(expected, ModBinaries.IsFor(fileName, platform));

    [Fact]
    public async Task ImportingIntoTheCurrentProfileTakesModsOverWithoutTouchingTheirFiles()
    {
        PlaceForeign("Mods/SML", "3.12.0");
        PlaceForeign("Mods/GameFeatures/MapZoom", "2.1.0", "SML");
        var dll = _game.ModFolder("Mods/GameFeatures/MapZoom/Binaries/mod.dll");
        var writtenAt = File.GetLastWriteTimeUtc(dll);
        await Profiles().CreateAsync(_install, "Playthrough");

        var result = await Profiles().ImportToCurrentAsync(_install, await Found());

        Assert.Equal(["MapZoom", "SML"], result.Adopted.Select(m => m.Reference).Order());
        Assert.Equal("binary MapZoom", File.ReadAllText(dll));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(dll));
        Assert.All(Installer().Check().Mods, m => Assert.Equal(ModHealth.Ok, m.Health));
        Assert.Empty(Installer().Check().Unmanaged);

        // SML is there because MapZoom needs it, so it goes once nothing does.
        Assert.Equal(["MapZoom"], Installer().Requested);
        Assert.Equal("Playthrough", result.Profile.Name);
        Assert.Equal(["MapZoom 2.1.0", "SML 3.12.0"], _store.Get("Playthrough")!.Mods.Select(m => $"{m.Name} {m.Version}"));
        Assert.Empty(await Found());
    }

    [Fact]
    public async Task ImportingIntoANewProfileAndSwitchingMakesItTheGamesProfile()
    {
        PlaceForeign("Mods/SML", "3.12.0");

        await Profiles().ImportToNewAsync(_install, await Found(), "  From SMM ", switchTo: true);

        Assert.Equal("From SMM", _store.ActiveName(_game.Root));
        Assert.Equal(["SML"], _store.Get("from smm")!.Mods.Select(m => m.Name));
        Assert.Single(Installer().Installed);
    }

    [Fact]
    public async Task ImportingIntoANewProfileWithoutSwitchingLeavesTheModsAloneAndStopsAsking()
    {
        PlaceForeign("Mods/SML", "3.12.0");

        var result = await Profiles().ImportToNewAsync(_install, await Found(), "Later", switchTo: false);

        Assert.Empty(result.Adopted);
        Assert.Empty(Installer().Installed);
        Assert.Null(_store.ActiveName(_game.Root));
        Assert.Equal(["SML"], _store.Get("Later")!.Mods.Select(m => m.Name));
        Assert.Empty(await Found());
        Assert.NotNull(_game.Read("Mods/SML"));
    }

    [Fact]
    public async Task NewProfileNamesMustBeUnused()
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
    public void ProfileFileNamesAreSafeOnEveryOS(string name, string file)
    {
        _store.Save(new(name, []));

        Assert.Equal([file], Directory.EnumerateFiles(_store.Folder).Select(Path.GetFileName));
        Assert.Equal(name, _store.Get(name)!.Name);
    }

    [Fact]
    public async Task DeletionLeavesInstalledModsAndRecordsIntactAndDoesNotRecreateTheProfile()
    {
        PlaceForeign("Mods/SML", "3.12.0");
        var profiles = Profiles();
        await profiles.ImportToNewAsync(_install, await Found(), "Delete", switchTo: true);
        var dll = _game.ModFolder("Mods/SML/Binaries/mod.dll");
        var writtenAt = File.GetLastWriteTimeUtc(dll);

        await profiles.DeleteAsync("Delete", [_install]);
        await profiles.RecordInstalledAsync(_install);

        Assert.Empty(_store.List());
        Assert.Null(_store.ActiveName(_game.Root));
        Assert.Equal("binary SML", File.ReadAllText(dll));
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(dll));
        Assert.Single(Installer().Installed);
    }

    [Fact]
    public async Task DeletionRefusesABusyInstallationAndReleasesEarlierReservations()
    {
        var installs = Installs();
        var profiles = new GameProfiles(_store, installs);
        _store.Save(new("Keep", []));
        var other = _install with { GamePath = _dir.CreateGame("Other") };
        using var busy = installs.BeginOperation(other);

        await Assert.ThrowsAsync<InstallationBusyException>(() => profiles.DeleteAsync("Keep", [_install, other]));

        Assert.NotNull(_store.Get("Keep"));
        Assert.False(installs.IsChanging(_install));
        Assert.True(installs.IsChanging(other));
    }

    [Fact]
    public async Task PendingRecoveryRefusesDeletionBeforeChangingTheLibrary()
    {
        _store.Save(new("Keep", []));
        _store.SetActive(_game.Root, "Keep");
        var activation = Path.Combine(_game.FactoryGame, GameModFolders.PROFILE_ACTIVATION);
        Directory.CreateDirectory(Path.GetDirectoryName(activation)!);
        File.WriteAllText(activation, "{}");

        await Assert.ThrowsAsync<InstallException>(() => Profiles().DeleteAsync("Keep", [_install]));

        Assert.NotNull(_store.Get("Keep"));
        Assert.Equal("Keep", _store.ActiveName(_game.Root));
    }

    [Fact]
    public async Task RecordsCreatingAndDeletingAProfile()
    {
        var activity = new ActivityLog(_dir.Combine("activity.jsonl"));
        var profiles = Profiles(activity);

        await profiles.CreateAsync(_install, "Atria");
        await profiles.DeleteAsync("Atria", [_install]);

        var entries = await activity.ReadAsync();
        Assert.Equal(["Deleted profile Atria", "Created profile Atria (0 mods)"], entries.Select(e => e.Summary));
        Assert.Equal([ActivityActions.PROFILE_DELETED, ActivityActions.PROFILE_CREATED], entries.Select(e => e.Action));
        Assert.Equal([null, _install.GamePath], entries.Select(e => e.InstallKey));
    }

    [Fact]
    public async Task RecordsSavingFoundModsAsAProfile()
    {
        var activity = new ActivityLog(_dir.Combine("activity.jsonl"));
        PlaceForeign("Mods/SML", "3.12.0");

        await Profiles(activity).ImportToNewAsync(_install, await Found(), "Later", switchTo: false);

        var entry = Assert.Single(await activity.ReadAsync());
        Assert.Equal((ActivityActions.MODS_TAKEN_OVER, "Saved 1 found mod as profile Later"), (entry.Action, entry.Summary));
        Assert.Equal(["SML 3.12.0"], entry.Details);
    }
}
