using ADA.Core.Activity;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Activity;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Servers;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Servers;

public sealed class ServerConnectionsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ServerStore _store;
    private readonly FakeSecretStore _secrets = new();
    private readonly FakeHttpHandler _http = new();
    private readonly ServerConnections _connections;

    public ServerConnectionsTests()
    {
        _store = new ServerStore(_dir.Combine("servers.json"));
        _connections = new ServerConnections(_store, new ServerSecrets(_secrets), new SatisfactoryServerApi(new HttpClient(_http)));
    }

    public void Dispose()
    {
        _connections.Dispose();
        _dir.Dispose();
    }

    // Port 1 on this machine: nothing listens there, so connecting fails at once.
    private static ServerConnection Unreachable(bool connected = false) => new(Guid.NewGuid(), "Home", "127.0.0.1", 1, "steam",
        ServerAuth.Password, "/srv/satisfactory", GameHost.Linux, "SHA256:abc", Connected: connected);

    private static GameInstall InstallFor(ServerConnection server) => new(ServerConnections.InstallKey(server.Id), GameLauncher.Unknown,
        server.Host, GameKind.DedicatedServer, null, server.Name);

    private ServerConnections Recording(ActivityLog activity) =>
        new(_store, new ServerSecrets(_secrets), new SatisfactoryServerApi(new HttpClient(_http)), activity: activity);

    [Fact]
    public async Task RecordsAFailedConnectWithTheReason()
    {
        var activity = new ActivityLog(_dir.Combine("activity.jsonl"));
        using var connections = Recording(activity);
        var server = Unreachable();
        _store.Save(server);
        _secrets.Saved[$"server:{server.Id:N}"] = "hunter2";

        await Assert.ThrowsAsync<ServerConnectException>(() => connections.ConnectAsync(server.Id));

        var entry = Assert.Single(await activity.ReadAsync());
        Assert.Equal((ActivityArea.Servers, ActivityOutcome.Failed, "Connecting to Home failed"), (entry.Area, entry.Outcome, entry.Summary));
        Assert.StartsWith("Couldn't reach 127.0.0.1:1", entry.Error);
        Assert.Equal(ServerConnections.InstallKey(server.Id), entry.InstallKey);
    }

    [Fact]
    public async Task AutomaticReconnectsAreNotRecorded()
    {
        var activity = new ActivityLog(_dir.Combine("activity.jsonl"));
        using var connections = Recording(activity);
        var server = Unreachable(connected: true);
        _store.Save(server);
        _secrets.Saved[$"server:{server.Id:N}"] = "hunter2";

        await connections.ResumeAsync();

        Assert.Equal(ServerState.Unavailable, connections.State);
        Assert.Empty(await activity.ReadAsync());
    }

    [Fact]
    public void InstallKeysAreRemoteAndShowTheServersName()
    {
        var install = InstallFor(Unreachable());

        Assert.True(install.IsRemote);
        Assert.StartsWith(GameInstall.REMOTE_SCHEME, install.GamePath);
        Assert.Equal("Home (Dedicated Server)", install.DisplayName);
    }

    [Fact]
    public async Task ConnectingWithoutASavedPasswordAsksForItAndMarksNothingConnected()
    {
        var server = Unreachable();
        _store.Save(server);

        var e = await Assert.ThrowsAsync<ServerConnectException>(() => _connections.ConnectAsync(server.Id));

        Assert.Equal("Enter the password for Home to connect.", e.Message);
        Assert.Null(_store.Connected);
        Assert.Equal(ServerState.Disconnected, _connections.State);
    }

    [Fact]
    public async Task AnUnreachableServerChosenByTheUserIsReportedAndNotMarkedConnected()
    {
        var server = Unreachable();
        _store.Save(server);
        _secrets.Saved[$"server:{server.Id:N}"] = "hunter2";

        var e = await Assert.ThrowsAsync<ServerConnectException>(() => _connections.ConnectAsync(server.Id));

        Assert.StartsWith("Couldn't reach 127.0.0.1:1", e.Message);
        Assert.Null(_store.Connected);
    }

    [Fact]
    public async Task AServerLeftConnectedThatCantBeReachedOnLaunchStaysMarkedAndIsUnavailable()
    {
        var server = Unreachable(connected: true);
        _store.Save(server);
        _secrets.Saved[$"server:{server.Id:N}"] = "hunter2";

        await _connections.ResumeAsync();

        Assert.Equal(ServerState.Unavailable, _connections.State);
        Assert.Equal(server.Id, _connections.Server?.Id);
        Assert.StartsWith("Couldn't reach", _connections.Problem);
        Assert.Equal(server.Id, _store.Connected?.Id);
        var unavailable = Assert.Throws<ServerUnavailableException>(() => _connections.FilesFor(InstallFor(server)));
        Assert.Equal(_connections.Problem, unavailable.Message);
    }

    [Fact]
    public async Task DisconnectStopsReconnectingOnLaunch()
    {
        var server = Unreachable(connected: true);
        _store.Save(server);
        _secrets.Saved[$"server:{server.Id:N}"] = "hunter2";
        await _connections.ResumeAsync();

        await _connections.DisconnectAsync();

        Assert.Null(_store.Connected);
        Assert.Equal(ServerState.Disconnected, _connections.State);
        Assert.Null(_connections.Server);
    }

    [Fact]
    public void FilesForAServerThatIsntConnectedSaysToConnectFirst()
    {
        var e = Assert.Throws<ServerUnavailableException>(() => _connections.FilesFor(InstallFor(Unreachable())));

        Assert.Equal("Connect to this server on the Servers page first.", e.Message);
    }

    [Fact]
    public async Task AServerThatIsntConnectedIsNeverReportedRunning()
    {
        Assert.False(await _connections.IsRunningAsync(InstallFor(Unreachable())));
        Assert.Empty(_http.RequestBodies);
    }

    [Fact]
    public async Task RunningChecksRouteLocalAndRemoteInstalls()
    {
        var localAsked = 0;
        var checks = new GameRunningChecks(_connections, _ =>
        {
            localAsked++;
            return Task.FromResult(true);
        });
        var local = new GameInstall(_dir.Combine("Game"), GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "Steam");

        Assert.True(await checks.IsRunningAsync(local, CancellationToken.None));
        Assert.False(await checks.IsRunningAsync(InstallFor(Unreachable()), CancellationToken.None));
        Assert.Equal(1, localAsked);
    }

    [Fact]
    public void FilesFactoryWithoutServersRefusesRemoteInstalls()
    {
        var e = Assert.Throws<ServerUnavailableException>(() => new GameFilesFactory().For(InstallFor(Unreachable())));

        Assert.Equal("Servers aren't available here.", e.Message);
    }
}
