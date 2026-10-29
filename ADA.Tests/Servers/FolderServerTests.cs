using System.Text.Json;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Servers;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Servers;

/// <summary>A server in a folder on this PC or a mounted share: set up, connected, locked and changed without SFTP.</summary>
public sealed class FolderServerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ServerStore _store;
    private readonly ServerConnections _connections;

    public FolderServerTests()
    {
        _store = new ServerStore(_dir.Combine("servers.json"));
        _connections = new ServerConnections(_store, new ServerSecrets(new FakeSecretStore()),
            new SatisfactoryServerApi(new HttpClient(new FakeHttpHandler())));
    }

    public void Dispose()
    {
        _connections.Dispose();
        _dir.Dispose();
    }

    /// <summary>A Linux dedicated server as SteamCMD lays it out, with a real-sized server program unless told otherwise.</summary>
    private string PlaceServer(string relative, string program = "ELF server program")
    {
        var root = _dir.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.Combine(root, "FactoryGame"));
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries", "Linux"));
        File.WriteAllText(Path.Combine(root, "FactoryServer.sh"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(root, "Engine", "Binaries", "Linux", "FactoryServer-Linux-Shipping"), program);
        File.WriteAllText(Path.Combine(root, "Engine", "Binaries", "Linux", "FactoryServer-Linux-Shipping.version"),
            """{ "Changelist": 513885, "GameVersion": "1.2.4.1" }""");
        return root;
    }

    /// <summary>A game client folder: Steam's launcher and client program.</summary>
    private string PlaceClient(string relative)
    {
        var root = _dir.Combine(relative.Split('/'));
        Directory.CreateDirectory(Path.Combine(root, "FactoryGame", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(root, "FactoryGameSteam.exe"), "launcher");
        File.WriteAllText(Path.Combine(root, "FactoryGame", "Binaries", "Win64", "FactoryGameSteam-Win64-Shipping.exe"), "client");
        return root;
    }

    private static ServerConnection Folder(string root, bool connected = false) => new(Guid.NewGuid(), "Home", "", 22, "",
        ServerAuth.Password, root, GameHost.Linux, "", Connected: connected, Transport: ServerTransport.Folder);

    private Task<SetupResult> Check(string entered, IReadOnlyCollection<string>? detected = null) =>
        new ServerSetupCheck(_connections, new SatisfactoryServerApi(new HttpClient(new FakeHttpHandler())))
            .RunAsync(Folder(entered), null, detectedRoots: detected);

    private static string Steps(SetupResult result) => string.Join("\n", result.Steps.Select(s => $"{s.Name}: {s.Detail}"));

    [Fact]
    public async Task Setup_finds_the_server_below_the_folder_and_warns_it_cant_tell_if_its_running()
    {
        var root = PlaceServer("share/steamapps/common/SatisfactoryDedicatedServer");

        var result = await Check(_dir.Combine("share"));

        Assert.True(result.Passed, Steps(result));
        Assert.Equal(root, result.GameRoot);
        Assert.Equal(GameHost.Linux, result.Host);
        Assert.Equal(513885, result.Build?.Changelist);
        Assert.Null(result.HostKeyFingerprint);
        Assert.Contains(result.Steps, s => s is { Name: "Server API", Passed: false, Blocking: false });
    }

    [Fact]
    public async Task A_game_install_is_refused_even_with_a_server_launcher_added()
    {
        var client = PlaceClient("Satisfactory");
        Directory.CreateDirectory(Path.Combine(client, "Engine", "Binaries", "Linux"));
        File.WriteAllText(Path.Combine(client, "FactoryServer.exe"), "");
        File.WriteAllText(Path.Combine(client, "Engine", "Binaries", "Linux", "FactoryServer-Linux-Shipping"), "renamed");

        var result = await Check(client);

        Assert.False(result.Passed);
        Assert.Null(result.GameRoot);
        Assert.StartsWith("This is a game install, not a dedicated server.", result.Steps[^1].Detail);
    }

    [Fact]
    public async Task An_epic_game_install_is_refused()
    {
        var root = _dir.Combine("Epic");
        Directory.CreateDirectory(Path.Combine(root, "FactoryGame"));
        File.WriteAllText(Path.Combine(root, "FactoryGameEGS.exe"), "launcher");

        var result = await Check(root);

        Assert.StartsWith("This is a game install", result.Steps[^1].Detail);
    }

    [Fact]
    public async Task Searching_skips_a_game_install_beside_a_real_server()
    {
        PlaceClient("games/Satisfactory");
        var server = PlaceServer("games/SatisfactoryDedicatedServer");

        var result = await Check(_dir.Combine("games"));

        Assert.True(result.Passed, Steps(result));
        Assert.Equal(server, result.GameRoot);
    }

    [Fact]
    public async Task A_launcher_without_the_servers_program_isnt_a_server()
    {
        var root = PlaceServer("Server", program: "");

        var result = await Check(root);

        Assert.False(result.Passed);
        Assert.Equal("Dedicated server", result.Steps[^1].Name);
        Assert.Null(result.GameRoot);
    }

    [Fact]
    public async Task A_server_already_detected_gets_a_warning_and_a_missing_folder_says_to_mount_it()
    {
        var root = PlaceServer("Steam/SatisfactoryDedicatedServer");

        var detected = await Check(root, [root]);
        var missing = await Check(_dir.Combine("Volumes", "Unmounted"));

        Assert.True(detected.Passed);
        Assert.Contains(detected.Steps, s => s is { Name: "Already listed", Blocking: false });
        Assert.False(missing.Passed);
        Assert.Contains("check the share is mounted", missing.Steps[0].Detail);
    }

    [Fact]
    public async Task Connecting_reads_the_build_and_a_folder_that_goes_away_becomes_unavailable()
    {
        var root = PlaceServer("Server");
        var server = Folder(root);
        _store.Save(server);

        await _connections.ConnectAsync(server.Id);

        Assert.Equal(ServerState.Connected, _connections.State);
        Assert.Equal(513885, _connections.Install?.Build?.Changelist);
        Assert.False(await _connections.IsRunningAsync(_connections.Install!));

        Directory.Move(root, root + "-unmounted");
        await _connections.CheckReachableAsync();

        Assert.Equal(ServerState.Unavailable, _connections.State);
        Assert.Contains("check the share is mounted", _connections.Problem);
    }

    [Fact]
    public async Task A_missing_folder_is_refused_on_connect_and_unavailable_on_launch()
    {
        var gone = Folder(_dir.Combine("Unmounted"));
        _store.Save(gone);

        await Assert.ThrowsAsync<ServerConnectException>(() => _connections.ConnectAsync(gone.Id));

        _store.Save(gone with { Connected = true });
        await _connections.ResumeAsync();
        Assert.Equal(ServerState.Unavailable, _connections.State);
    }

    [Fact]
    public async Task The_lock_is_a_file_that_names_its_holder_and_can_be_taken_over()
    {
        var root = PlaceServer("Server");
        var server = Folder(root);
        _store.Save(server);
        await _connections.ConnectAsync(server.Id);
        var factoryGame = Path.Combine(root, "FactoryGame");
        var files = new FolderGameFiles(factoryGame, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        files.CreateDirectory(GameModFolders.WORK);

        using (files.AcquireLock(GameModFolders.LOCK))
        {
            Assert.Throws<LockHeldException>(() => files.AcquireLock(GameModFolders.LOCK));
            Assert.True(File.Exists(Path.Combine(factoryGame, ".ada", "lock")));
        }
        Assert.False(File.Exists(Path.Combine(factoryGame, ".ada", "lock")));

        File.WriteAllText(Path.Combine(factoryGame, ".ada", "lock"),
            JsonSerializer.Serialize(new LockHolder("OTHER-PC", 1234, DateTimeOffset.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var e = Assert.Throws<LockHeldException>(() => files.AcquireLock(GameModFolders.LOCK));
        Assert.StartsWith("OTHER-PC has been changing this server's mods since", e.Message);
        Assert.Equal("OTHER-PC", (await _connections.LockHolderAsync())?.Machine);

        await _connections.BreakLockAsync();
        Assert.Null(await _connections.LockHolderAsync());
        files.AcquireLock(GameModFolders.LOCK).Dispose();
    }

    [Fact]
    public async Task Mods_install_and_uninstall_through_the_folder_with_server_builds()
    {
        var root = PlaceServer("Server");
        var server = Folder(root);
        _store.Save(server);
        await _connections.ConnectAsync(server.Id);
        var installs = new ModInstallService(new ADA.Infrastructure.Settings.SettingsStore(_dir.Combine("settings.json")),
            new(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), (_, _) => Task.FromResult(false),
            files: new GameFilesFactory(_connections));
        var game = _connections.Install!;
        var smod = new SmodBuilder("SML", "3.12.0").Typical().WriteTo(_dir.Combine("Published"));

        var installer = installs.InstallerFor(game);
        installer.Apply([new ModChange.Install(smod, ModInstallService.TargetFor(game))]);

        Assert.Equal(TargetPlatform.LinuxServer, ModInstallService.TargetFor(game));
        Assert.True(File.Exists(Path.Combine(root, "FactoryGame", "Mods", "SML", "SML.uplugin")));
        Assert.All(installer.Check(full: true).Mods, m => Assert.Equal(ModHealth.Ok, m.Health));

        installer.Apply([new ModChange.Uninstall("SML")]);
        Assert.False(Directory.Exists(Path.Combine(root, "FactoryGame", "Mods", "SML")));
    }

    [Theory]
    [InlineData(GameHost.Linux, ServerTransport.Sftp, TargetPlatform.LinuxServer)]
    [InlineData(GameHost.Windows, ServerTransport.Sftp, TargetPlatform.WindowsServer)]
    [InlineData(GameHost.Linux, ServerTransport.Folder, TargetPlatform.LinuxServer)]
    [InlineData(GameHost.Windows, ServerTransport.Folder, TargetPlatform.WindowsServer)]
    [InlineData(GameHost.CrossOver, ServerTransport.Folder, TargetPlatform.WindowsServer)]
    [InlineData(GameHost.LinuxCompat, ServerTransport.Sftp, TargetPlatform.WindowsServer)]
    public void A_saved_server_is_always_a_dedicated_server_with_server_builds(GameHost host, ServerTransport transport, TargetPlatform expected)
    {
        var install = ServerConnections.ToInstall(Folder("/srv") with { Host = host, Transport = transport }, null);

        Assert.Equal(GameKind.DedicatedServer, install.Kind);
        Assert.True(install.IsRemote);
        Assert.Equal(expected, ModInstallService.TargetFor(install));
        Assert.NotEqual(TargetPlatform.Windows, ModInstallService.TargetFor(install));
    }

    [Fact]
    public void An_older_saved_server_loads_as_sftp_and_a_folder_server_round_trips()
    {
        var path = _dir.WriteFile("old/servers.json", $$"""
            [{ "id": "{{Guid.NewGuid()}}", "name": "Old", "address": "1.2.3.4", "port": 22, "user": "steam", "auth": "Password",
               "gameRoot": "/srv", "host": "Linux", "hostKeyFingerprint": "abc" }]
            """);
        Assert.Equal(ServerTransport.Sftp, Assert.Single(new ServerStore(path).All).Transport);

        var folder = Folder("/Volumes/Satisfactory/Server");
        _store.Save(folder);
        var saved = Assert.Single(new ServerStore(_dir.Combine("servers.json")).All);
        Assert.Equal(ServerTransport.Folder, saved.Transport);
        Assert.Null(_store.ProblemWith(folder));
        Assert.DoesNotContain("isFolder", File.ReadAllText(_dir.Combine("servers.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_lock_file_is_held_by_an_unknown_ada_and_can_be_taken_over()
    {
        var root = PlaceServer("Server");
        var server = Folder(root);
        _store.Save(server);
        await _connections.ConnectAsync(server.Id);
        var factoryGame = Path.Combine(root, "FactoryGame");
        Directory.CreateDirectory(Path.Combine(factoryGame, ".ada"));
        File.WriteAllText(Path.Combine(factoryGame, ".ada", "lock"), "");
        var files = new FolderGameFiles(factoryGame, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Throws<LockHeldException>(() => files.AcquireLock(GameModFolders.LOCK));
        Assert.True((await _connections.LockHolderAsync())?.IsUnknown);
        Assert.DoesNotContain("isUnknown", JsonSerializer.Serialize(new LockHolder("PC", 1, DateTimeOffset.UnixEpoch),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);

        await _connections.BreakLockAsync();
        files.AcquireLock(GameModFolders.LOCK).Dispose();
    }

    [Fact]
    public async Task A_folder_that_goes_away_is_noticed_by_the_connections_own_timer()
    {
        using var connections = new ServerConnections(_store, new ServerSecrets(new FakeSecretStore()),
            new SatisfactoryServerApi(new HttpClient(new FakeHttpHandler()))) { ReachabilityInterval = TimeSpan.FromMilliseconds(20) };
        var root = PlaceServer("Server");
        var server = Folder(root);
        _store.Save(server);
        await connections.ConnectAsync(server.Id);

        Directory.Move(root, root + "-unmounted");

        await Wait.Until(() => connections.State == ServerState.Unavailable);
        Assert.Contains("check the share is mounted", connections.Problem);
    }

    [Fact]
    public async Task A_cancelled_connect_leaves_nothing_connected()
    {
        var root = PlaceServer("Server");
        var server = Folder(root);
        _store.Save(server);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _connections.ConnectAsync(server.Id, new CancellationToken(canceled: true)));

        Assert.NotEqual(ServerState.Connected, _connections.State);
    }
}
