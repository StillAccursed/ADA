using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Servers;
using ADA.Infrastructure.Settings;
using ADA.Platform.Detection;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

/// <summary>The connected server as an install on the Dashboard: listed, selected, and reported when it can't be reached.</summary>
public sealed class DashboardServerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ServerStore _store;
    private readonly FakeSecretStore _secrets = new();
    private readonly ServerConnections _connections;
    private readonly ModInstallService _installs;
    private readonly ProfileStore _profileStore;
    private readonly GameProfiles _profiles;
    private readonly GameInstall _local;
    private readonly Dictionary<string, List<CatalogVersion>> _catalog = new(StringComparer.OrdinalIgnoreCase);

    public DashboardServerTests()
    {
        _store = new ServerStore(_dir.Combine("servers.json"));
        _connections = new ServerConnections(_store, new ServerSecrets(_secrets), new SatisfactoryServerApi(new HttpClient(new FakeHttpHandler())));
        var settings = new SettingsStore(_dir.Combine("settings.json"));
        _installs = new ModInstallService(settings, new(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), (_, _) => Task.FromResult(false));
        _profileStore = new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        _profiles = new GameProfiles(_profileStore, _installs);
        _local = new GameInstall(_dir.CreateGame("Satisfactory"), GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "Steam",
            new GameBuild(513885, "1.2"));
    }

    public void Dispose()
    {
        _connections.Dispose();
        _dir.Dispose();
    }

    private DashboardViewModel Dashboard(Func<CancellationToken, Task<IReadOnlyList<GameProcess>>>? findRunning = null) =>
        Dashboard(_installs, _profiles, findRunning);

    private DashboardViewModel Dashboard(ModInstallService installs, GameProfiles profiles,
        Func<CancellationToken, Task<IReadOnlyList<GameProcess>>>? findRunning = null) =>
        DashboardFactory.Create(_dir, new OneGame(_local),
            game => new GameInstallViewModel(game, installs,
                new ModResolutionService(new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), installs),
                profiles, NullLogger<GameInstallViewModel>.Instance),
            installs: installs, profiles: profiles, findRunning: findRunning, watchInterval: TimeSpan.FromMilliseconds(20),
            servers: _connections, sync: new ProfileSync(_profileStore, installs,
                new FakeVersionSource((r, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(r) ?? []))));

    // Port 1 on this machine: nothing listens there, so it's always unavailable.
    private ServerConnection LeftConnected()
    {
        var server = new ServerConnection(Guid.NewGuid(), "Home", "127.0.0.1", 1, "steam", ServerAuth.Password, "/srv", GameHost.Linux,
            "abc", Connected: true);
        _store.Save(server);
        _secrets.Saved[$"server:{server.Id:N}"] = "hunter2";
        return server;
    }

    [Fact]
    public async Task AServerThatCantBeReachedIsListedAfterLocalInstallsAndNeedsAttention()
    {
        var server = LeftConnected();
        var dashboard = Dashboard();
        await dashboard.ScanCommand.ExecuteAsync(null);

        await _connections.ResumeAsync();
        await Wait.Until(() => dashboard.Installs.Count == 2 && dashboard.ServerProblem is not null);

        Assert.Equal([_local.GamePath, ServerConnections.InstallKey(server.Id)], dashboard.Installs.Select(i => i.Install.GamePath));
        var entry = dashboard.Installs[1];
        Assert.True(entry.IsUnavailable);
        Assert.Equal("Home (Dedicated Server)", entry.Install.DisplayName);
        Assert.StartsWith("Home is unavailable: Couldn't reach", dashboard.ServerProblem);
        Assert.Contains(dashboard.ServerProblem!, dashboard.Notifications);
        Assert.Equal("1 install found.", dashboard.StatusText);
    }

    [Fact]
    public async Task ARescanKeepsTheServerAndItsSelection()
    {
        var server = LeftConnected();
        var dashboard = Dashboard();
        await dashboard.ScanCommand.ExecuteAsync(null);
        await _connections.ResumeAsync();
        dashboard.SelectInstall(ServerConnections.InstallKey(server.Id));
        await Wait.Until(() => dashboard.SelectedInstall?.Install.IsRemote == true);

        await dashboard.ScanCommand.ExecuteAsync(null);

        Assert.Equal(2, dashboard.Installs.Count);
        Assert.True(dashboard.SelectedInstall!.Install.IsRemote);
    }

    [Fact]
    public async Task DisconnectingTheSelectedServerSelectsTheLocalInstall()
    {
        var server = LeftConnected();
        var dashboard = Dashboard();
        await dashboard.ScanCommand.ExecuteAsync(null);
        await _connections.ResumeAsync();
        dashboard.SelectInstall(ServerConnections.InstallKey(server.Id));
        await Wait.Until(() => dashboard.SelectedInstall?.Install.IsRemote == true);

        await _connections.DisconnectAsync();
        await Wait.Until(() => dashboard.Installs.Count == 1);

        Assert.Single(dashboard.Installs);
        Assert.Same(dashboard.Installs[0], dashboard.SelectedInstall);
        Assert.Null(dashboard.ServerProblem);
    }

    [Fact]
    public async Task ASelectionAskedForBeforeTheServerIsListedIsAppliedWhenItAppears()
    {
        var server = LeftConnected();
        var dashboard = Dashboard();
        await dashboard.ScanCommand.ExecuteAsync(null);

        dashboard.SelectInstall(ServerConnections.InstallKey(server.Id));
        Assert.False(dashboard.SelectedInstall!.Install.IsRemote);
        await _connections.ResumeAsync();

        await Wait.Until(() => dashboard.SelectedInstall?.Install.IsRemote == true);
    }

    [Fact]
    public async Task AnUnavailableServerCantChooseProfilesAndItsDiskCardIsntALocalFolder()
    {
        var server = LeftConnected();
        var dashboard = Dashboard();
        await dashboard.ScanCommand.ExecuteAsync(null);
        await _connections.ResumeAsync();
        dashboard.SelectInstall(ServerConnections.InstallKey(server.Id));
        await Wait.Until(() => dashboard.SelectedInstall?.Install.IsRemote == true);
        await dashboard.RefreshCardsAsync();

        Assert.False(dashboard.CanChooseProfile);
        Assert.Equal("Unavailable", dashboard.ServerStateText);
        Assert.Equal("ADA-installed mods", dashboard.DiskModsLabel);
        Assert.Equal("On the server", dashboard.DiskVolumeLabel);
        Assert.Equal("Unknown", dashboard.Disk.ModsSize);
        Assert.Equal("Unknown", dashboard.Disk.AvailableSpace);
    }

    [Fact]
    public async Task ALocalGameRunningDoesntMarkTheServerRunning()
    {
        LeftConnected();
        var dashboard = Dashboard(_ => Task.FromResult<IReadOnlyList<GameProcess>>([new GameProcess(1, "FactoryGameSteam")]));
        await dashboard.ScanCommand.ExecuteAsync(null);
        await _connections.ResumeAsync();
        await Wait.Until(() => dashboard.Installs.Count == 2);
        using var stop = new CancellationTokenSource();

        var watching = dashboard.WatchGameAsync(stop.Token);
        for (var i = 0; i < 200 && !dashboard.Installs[0].IsGameRunning; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(dashboard.IsGameRunning);
        Assert.True(dashboard.Installs[0].IsGameRunning);
        Assert.False(dashboard.Installs[1].IsGameRunning);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
    }

    [Fact]
    public async Task AServerOnAnotherProfileIsNamedWithBothProfiles()
    {
        var server = LeftConnected();
        _profileStore.SetActive(ServerConnections.InstallKey(server.Id), "Dedicated");
        _profileStore.SetActive(_local.GamePath, "Mine");
        var dashboard = Dashboard();
        await dashboard.ScanCommand.ExecuteAsync(null);

        await _connections.ResumeAsync();

        await Wait.Until(() => dashboard.SyncText is not null);
        Assert.Equal("Home uses \"Dedicated\"; this game uses \"Mine\".", dashboard.SyncText);
        Assert.False(dashboard.IsOutOfSync);
    }

    [Fact]
    public async Task ASharedProfileWithAnUnreachableServerCantBeChecked()
    {
        var server = LeftConnected();
        _profileStore.SetActive(ServerConnections.InstallKey(server.Id), "Playthrough");
        _profileStore.SetActive(_local.GamePath, "Playthrough");
        var dashboard = Dashboard();
        await dashboard.ScanCommand.ExecuteAsync(null);

        await _connections.ResumeAsync();

        await Wait.Until(() => dashboard.SyncText is not null);
        Assert.Equal("Home can't be checked right now.", dashboard.SyncText);
        Assert.False(dashboard.UpdateSyncTargetCommand.CanExecute(null));

        await dashboard.OfferServerUpdateAsync(dashboard.Installs[0]);
        Assert.Null(dashboard.SyncQuestion);
    }

    [SftpFact]
    public async Task AConnectedServerMissingAModPlayersNeedIsOfferedAnUpdate()
    {
        using var test = new SftpTestServer();
        _store.Save(test.Server);
        _secrets.Saved[$"server:{test.Server.Id:N}"] = SftpTestServer.Password;
        var installs = new ModInstallService(new SettingsStore(_dir.Combine("sftp-settings.json")),
            new(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), (_, _) => Task.FromResult(false),
            files: new GameFilesFactory(_connections));
        var profiles = new GameProfiles(_profileStore, installs);
        _catalog["Storage"] = [CatalogFixtures.Version("Storage", "1.0.0") with { RequiredOnRemote = true }];
        _catalog["BetterGrass"] = [CatalogFixtures.Version("BetterGrass", "2.0.0") with { RequiredOnRemote = false }];
        _profileStore.Save(new("Playthrough", [new("Storage", "1.0.0"), new("BetterGrass", "2.0.0")]));
        _profileStore.SetActive(ServerConnections.InstallKey(test.Server.Id), "Playthrough");
        _profileStore.SetActive(_local.GamePath, "Playthrough");
        var dashboard = Dashboard(installs, profiles);
        await dashboard.ScanCommand.ExecuteAsync(null);

        await _connections.ConnectAsync(test.Server.Id);

        await Wait.Until(() => dashboard.IsOutOfSync);
        Assert.Same(dashboard.Installs[1], dashboard.SyncTarget);
        Assert.Equal("Test server differs from \"Playthrough\" in 1 mod players need: needs Storage 1.0.0.", dashboard.SyncText);
        Assert.Equal("Update Test server", dashboard.SyncActionText);

        var asking = dashboard.OfferServerUpdateAsync(dashboard.Installs[0]);
        await Wait.Until(() => dashboard.SyncQuestion is not null);
        Assert.Equal("Also apply to Test server?", dashboard.SyncQuestion!.Title);
        Assert.Contains("Storage 1.0.0", dashboard.SyncQuestion.Message);
        Assert.DoesNotContain("BetterGrass", dashboard.SyncQuestion.Message);
        dashboard.SyncQuestion.CancelCommand.Execute(null);
        await asking;
        Assert.Null(dashboard.SyncQuestion);
        Assert.Null(dashboard.SwitchPrompt);
    }

    private sealed class OneGame(GameInstall install) : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => [install];
    }
}
