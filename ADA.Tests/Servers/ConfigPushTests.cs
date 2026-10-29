using System.Text.Json;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Servers;
using ADA.Infrastructure.Settings;
using ADA.Platform.Detection;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Servers;

/// <summary>Pushing mod configs from a game to a folder server sharing its profile; no SFTP needed.</summary>
public sealed class ConfigPushTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ServerStore _store;
    private readonly ServerConnections _connections;
    private readonly ModInstallService _installs;
    private readonly GameInstall _game;
    private readonly string _serverRoot;
    private readonly ProfileStore _profileStore;
    private readonly GameProfiles _profiles;
    private readonly ModResolutionService _resolution;
    private bool _serverRunning;

    public ConfigPushTests()
    {
        _store = new ServerStore(_dir.Combine("servers.json"));
        _connections = new ServerConnections(_store, new ServerSecrets(new FakeSecretStore()),
            new SatisfactoryServerApi(new HttpClient(new FakeHttpHandler())));
        _installs = new ModInstallService(new SettingsStore(_dir.Combine("settings.json")),
            new(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), (_, _) => Task.FromResult(false),
            files: new GameFilesFactory(_connections));
        _game = new GameInstall(_dir.CreateGame("Satisfactory"), GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "Steam",
            new GameBuild(513885, "1.2"));
        _serverRoot = _dir.Combine("Server");
        Directory.CreateDirectory(Path.Combine(_serverRoot, "FactoryGame"));
        _profileStore = new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        _profiles = new GameProfiles(_profileStore, _installs);
        _resolution = new ModResolutionService(new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), _installs);
    }

    public void Dispose()
    {
        _connections.Dispose();
        _dir.Dispose();
    }

    private ConfigPush Push() => new(_installs, new GameFilesFactory(_connections), (_, _) => Task.FromResult(_serverRunning));

    private GameInstallViewModel Card(GameInstall install) =>
        new(install, _installs, _resolution, _profiles, NullLogger<GameInstallViewModel>.Instance);

    /// <summary>Connects the folder server and installs these mods on the game and on the server.</summary>
    private async Task<GameInstall> ConnectWith(string[] onGame, string[] onServer)
    {
        var saved = new ServerConnection(Guid.NewGuid(), "Home", "", 22, "", ServerAuth.Password, _serverRoot, GameHost.Linux, "",
            Transport: ServerTransport.Folder);
        _store.Save(saved);
        await _connections.ConnectAsync(saved.Id);
        var server = _connections.Install!;
        Install(_game, onGame);
        Install(server, onServer);
        return server;
    }

    private void Install(GameInstall game, string[] mods)
    {
        var changes = mods.Select(m => (ModChange)new ModChange.Install(
            new SmodBuilder(m, "1.0.0").Typical().WriteTo(_dir.Combine("Published", game.GamePath.GetHashCode().ToString(), m)),
            ModInstallService.TargetFor(game))).ToList();
        _installs.InstallerFor(game).Apply(changes);
    }

    private string GameConfig(string reference) => Path.Combine(_game.GamePath, "FactoryGame", "Configs", reference + ".cfg");

    private string ServerConfig(string reference) => Path.Combine(_serverRoot, "FactoryGame", "Configs", reference + ".cfg");

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task ListsConfigsThatDifferOrAreMissingForModsOnBothSidesIgnoringLineEndings()
    {
        var server = await ConnectWith(["SML", "Storage", "Trains", "BetterGrass"], ["SML", "Storage", "Trains", "ServerOnly"]);
        Write(GameConfig("SML"), "{\r\n  \"a\": 1\r\n}");
        Write(ServerConfig("SML"), "{\n  \"a\": 1\n}");
        Write(GameConfig("Storage"), "{ \"stack\": 500 }");
        Write(ServerConfig("Storage"), "{ \"stack\": 100 }");
        Write(GameConfig("Trains"), "{ }");
        Write(GameConfig("BetterGrass"), "{ \"dense\": true }");
        Write(ServerConfig("ServerOnly"), "{ }");

        var differences = await Push().CompareAsync(_game, server);

        Assert.Equal([new ConfigDifference("Storage", true), new ConfigDifference("Trains", false)], differences);
    }

    [Fact]
    public async Task PushingWritesOnlyTheChosenFilesAndRestoringPutsBackWhatTheyReplaced()
    {
        var server = await ConnectWith(["Storage", "Trains", "Vehicles"], ["Storage", "Trains", "Vehicles"]);
        Write(GameConfig("Storage"), "game storage");
        Write(ServerConfig("Storage"), "server storage");
        Write(GameConfig("Trains"), "game trains");
        Write(GameConfig("Vehicles"), "game vehicles");
        Write(ServerConfig("Vehicles"), "server vehicles");
        var push = Push();

        await push.PushAsync(_game, server, ["Storage", "Trains"]);

        Assert.Equal("game storage", File.ReadAllText(ServerConfig("Storage")));
        Assert.Equal("game trains", File.ReadAllText(ServerConfig("Trains")));
        Assert.Equal("server vehicles", File.ReadAllText(ServerConfig("Vehicles")));
        Assert.True(await push.HasBackupAsync(server));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_serverRoot, "FactoryGame"), "*.tmp", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(_serverRoot, "FactoryGame", ".ada", "lock")));

        await push.RestoreAsync(server);

        Assert.Equal("server storage", File.ReadAllText(ServerConfig("Storage")));
        Assert.Equal("game trains", File.ReadAllText(ServerConfig("Trains"))); // new on the server: nothing to go back to
        Assert.False(await push.HasBackupAsync(server));
    }

    [Fact]
    public async Task ASecondPushReplacesTheBackup()
    {
        var server = await ConnectWith(["Storage", "Trains"], ["Storage", "Trains"]);
        Write(GameConfig("Storage"), "game storage");
        Write(ServerConfig("Storage"), "server storage");
        Write(GameConfig("Trains"), "game trains");
        Write(ServerConfig("Trains"), "server trains");
        var push = Push();

        await push.PushAsync(_game, server, ["Storage"]);
        await push.PushAsync(_game, server, ["Trains"]);
        await push.RestoreAsync(server);

        Assert.Equal("game storage", File.ReadAllText(ServerConfig("Storage")));
        Assert.Equal("server trains", File.ReadAllText(ServerConfig("Trains")));
    }

    [Fact]
    public async Task PushingIsRefusedWhileTheServerRunsOrAnotherPcIsChangingIt()
    {
        var server = await ConnectWith(["Storage"], ["Storage"]);
        Write(GameConfig("Storage"), "game storage");
        Write(ServerConfig("Storage"), "server storage");

        _serverRunning = true;
        await Assert.ThrowsAsync<GameRunningException>(() => Push().PushAsync(_game, server, ["Storage"]));
        _serverRunning = false;

        File.WriteAllText(Path.Combine(_serverRoot, "FactoryGame", ".ada", "lock"),
            JsonSerializer.Serialize(new LockHolder("OTHER-PC", 1234, DateTimeOffset.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await Assert.ThrowsAsync<LockHeldException>(() => Push().PushAsync(_game, server, ["Storage"]));

        Assert.Equal("server storage", File.ReadAllText(ServerConfig("Storage")));
    }

    [Fact]
    public async Task TheDialogPushesTheTickedConfigsAndWaitsForTheServerToStop()
    {
        var server = await ConnectWith(["Storage", "Trains"], ["Storage", "Trains"]);
        Write(GameConfig("Storage"), "game storage");
        Write(GameConfig("Trains"), "game trains");
        var installVm = Card(_game);
        var serverVm = Card(server);
        serverVm.IsGameRunning = true;
        var push = Push();
        var dialog = new PushConfigsViewModel(push, installVm, serverVm, "Playthrough",
            await push.CompareAsync(_game, server), hasBackup: false);

        Assert.Equal(["Storage (not on the server yet)", "Trains (not on the server yet)"], dialog.Configs.Select(c => c.Label));
        Assert.StartsWith("Stop Home first", dialog.RunningText);
        Assert.False(dialog.PushCommand.CanExecute(null));

        serverVm.IsGameRunning = false;
        dialog.Configs[1].IsChosen = false;
        Assert.True(dialog.PushCommand.CanExecute(null));
        await dialog.PushCommand.ExecuteAsync(null);

        Assert.Equal("Pushed 1 config. Restart Home for them to take effect.", dialog.Outcome);
        Assert.True(File.Exists(ServerConfig("Storage")));
        Assert.False(File.Exists(ServerConfig("Trains")));
        Assert.True(dialog.HasBackup);

        bool? changed = null;
        dialog.Closed += (_, value) => changed = value;
        dialog.CloseCommand.Execute(null);
        Assert.True(changed);
    }

    [Fact]
    public async Task TheDashboardSaysHowManyConfigsDifferAndOpensTheDialog()
    {
        // The Dashboard learns of the server from the connection's changes, so it exists first, as in the app.
        var dashboard = DashboardFactory.Create(_dir, new OneGame(_game), Card, installs: _installs, profiles: _profiles,
            resolution: _resolution, watchInterval: TimeSpan.FromMilliseconds(20), servers: _connections,
            sync: new ProfileSync(_profileStore, _installs, new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([]))),
            configPush: Push());
        await dashboard.ScanCommand.ExecuteAsync(null);
        var server = await ConnectWith(["Storage"], ["Storage"]);
        Write(GameConfig("Storage"), "game storage");
        Write(ServerConfig("Storage"), "server storage");
        _profileStore.Save(new("Playthrough", [new("Storage", "1.0.0")]));
        _profileStore.SetActive(_game.GamePath, "Playthrough");
        _profileStore.SetActive(server.GamePath, "Playthrough");
        await dashboard.ScanCommand.ExecuteAsync(null);

        await Wait.Until(() => dashboard.ConfigsText is not null);
        Assert.Equal("1 mod config on the server differs from this game.", dashboard.ConfigsText);

        await dashboard.PushConfigsCommand.ExecuteAsync(null);
        var dialog = Assert.IsType<PushConfigsViewModel>(dashboard.ConfigsPrompt);
        Assert.Equal($"From {_game.DisplayName} · \"Playthrough\"", dialog.FromText);
        dialog.CloseCommand.Execute(null);
        Assert.Null(dashboard.ConfigsPrompt);
    }

    private sealed class OneGame(GameInstall install) : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => [install];
    }
}
