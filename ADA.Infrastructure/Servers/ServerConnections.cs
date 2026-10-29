using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ADA.Infrastructure.Servers;

public enum ServerState
{
    Disconnected,
    Connecting,
    Connected,

    /// <summary>The user left it connected, but it can't be reached; retried in the background.</summary>
    Unavailable,
}

/// <summary>The connected server can't be used right now; the message says why.</summary>
public sealed class ServerUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Connecting failed; the message says why and is shown as-is.</summary>
public sealed class ServerConnectException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The one server ADA is connected to. Connecting to another disconnects it first. The choice is
/// saved in <see cref="ServerStore"/>, so <see cref="ResumeAsync"/> reconnects it on launch and a
/// dropped connection is retried in the background until the user disconnects.
/// </summary>
/// <remarks><see cref="Changed"/> is raised on whichever thread changed the state; UI consumers marshal it.</remarks>
public sealed class ServerConnections(ServerStore store, ServerSecrets secrets, SatisfactoryServerApi api,
    ILogger<ServerConnections>? log = null) : IDisposable
{
    private static readonly TimeSpan CONNECT_TIMEOUT = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan OPERATION_TIMEOUT = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan KEEP_ALIVE = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] RETRY_DELAYS =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly ILogger _log = log ?? NullLogger<ServerConnections>.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SftpClient? _client;
    private CancellationTokenSource? _retry;

    public event EventHandler? Changed;

    /// <summary>The server that's connected, connecting or unavailable; null when none is.</summary>
    public ServerConnection? Server { get; private set; }

    public ServerState State { get; private set; } = ServerState.Disconnected;

    /// <summary>Why the server is <see cref="ServerState.Unavailable"/>; null otherwise.</summary>
    public string? Problem { get; private set; }

    /// <summary>The server as an install, for the sidebar and the Mods page, while it's connected.</summary>
    public GameInstall? Install { get; private set; }

    /// <summary>
    /// On launch: reconnects the server the user left connected. If it can't be reached, it stays
    /// marked connected, becomes <see cref="ServerState.Unavailable"/> and is retried in the background.
    /// </summary>
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        if (store.Connected is { } server)
        {
            await ConnectCoreAsync(server, explicitConnect: false, fromRetry: false, ct);
        }
    }

    /// <summary>
    /// The user chose Connect. Disconnects any other server first. On failure nothing is marked
    /// connected and the reason is thrown.
    /// </summary>
    /// <exception cref="ServerConnectException">It couldn't connect; the message says why.</exception>
    public Task ConnectAsync(Guid serverId, CancellationToken ct = default) =>
        ConnectCoreAsync(store.Find(serverId) ?? throw new ServerConnectException("That server is no longer saved."),
            explicitConnect: true, fromRetry: false, ct);

    /// <summary>The user chose Disconnect: closes the connection and stops reconnecting on launch.</summary>
    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Close();
            store.SetConnected(null);
            SetState(null, ServerState.Disconnected, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Retries an unavailable server now, rather than waiting for the next background attempt.</summary>
    public Task RetryAsync(CancellationToken ct = default) =>
        Server is { } server && State == ServerState.Unavailable ? ConnectCoreAsync(server, explicitConnect: false, fromRetry: false, ct) : Task.CompletedTask;

    /// <summary>Whether the server behind <paramref name="game"/> answers its API; false when it doesn't.</summary>
    public Task<bool> IsRunningAsync(GameInstall game, CancellationToken ct = default) =>
        Server is { } server && Matches(game, server) ? api.IsRunningAsync(server.Address, server.ApiPort, ct) : Task.FromResult(false);

    /// <summary>The connected server's files.</summary>
    /// <exception cref="ServerUnavailableException"><paramref name="game"/> isn't the connected server, or it's unreachable.</exception>
    internal IGameFiles FilesFor(GameInstall game)
    {
        if (Server is not { } server || !Matches(game, server))
        {
            throw new ServerUnavailableException("Connect to this server on the Servers page first.");
        }
        if (State != ServerState.Connected || _client is not { IsConnected: true } client)
        {
            throw new ServerUnavailableException(Problem ?? $"{server.Name} isn't connected right now.");
        }
        return new SftpGameFiles(client, server.GameRoot, _log);
    }

    /// <summary>The install key for a saved server; the same key profiles and queues use.</summary>
    public static string InstallKey(Guid serverId) => $"{GameInstall.REMOTE_SCHEME}{serverId:N}";

    public void Dispose()
    {
        _retry?.Cancel();
        Close();
        _gate.Dispose();
    }

    /// <param name="explicitConnect">The user chose Connect: a failure is thrown and nothing stays marked connected.</param>
    /// <param name="fromRetry">Called by the background retry loop, which must not cancel or restart itself.</param>
    private async Task ConnectCoreAsync(ServerConnection server, bool explicitConnect, bool fromRetry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!fromRetry)
            {
                _retry?.Cancel();
            }
            Close();
            SetState(server, ServerState.Connecting, null, null);
            try
            {
                var client = await OpenAsync(server, ct);
                var build = await Task.Run(() => ServerSetupCheck.ReadBuild(client, server.GameRoot), ct);
                _client = client;
                _client.ErrorOccurred += OnConnectionError;
                store.SetConnected(server.Id);
                SetState(server, ServerState.Connected, null, ToInstall(server, build));
                _log.LogInformation("Connected to {Server}", server.Name);
            }
            catch (ServerConnectException e) when (!explicitConnect)
            {
                _log.LogWarning("Couldn't reconnect to {Server}: {Reason}", server.Name, e.Message);
                SetState(server, ServerState.Unavailable, e.Message, null);
                if (!fromRetry)
                {
                    ScheduleRetry(server);
                }
            }
            catch (ServerConnectException)
            {
                SetState(null, ServerState.Disconnected, null, null);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <exception cref="ServerConnectException">Unreachable, refused the login, or its identity changed.</exception>
    internal async Task<SftpClient> OpenAsync(ServerConnection server, CancellationToken ct, string? secret = null,
        Action<string>? hostKeySeen = null)
    {
        secret ??= await secrets.LoadAsync(server.Id, ct);
        if (secret is null && server.Auth == ServerAuth.Password)
        {
            throw new ServerConnectException($"Enter the password for {server.Name} to connect.");
        }

        var client = new SftpClient(new ConnectionInfo(server.Address, server.Port, server.User, Login(server, secret)) { Timeout = CONNECT_TIMEOUT })
        {
            KeepAliveInterval = KEEP_ALIVE,
            OperationTimeout = OPERATION_TIMEOUT,
        };
        var identityChanged = false;
        client.HostKeyReceived += (_, e) =>
        {
            hostKeySeen?.Invoke(e.FingerPrintSHA256);
            // An empty saved fingerprint means "first contact": only the setup check passes one.
            e.CanTrust = server.HostKeyFingerprint.Length == 0 || e.FingerPrintSHA256 == server.HostKeyFingerprint;
            identityChanged = !e.CanTrust;
        };

        try
        {
            await client.ConnectAsync(ct);
            return client;
        }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or IOException)
        {
            client.Dispose();
            throw new ServerConnectException(e switch
            {
                _ when identityChanged =>
                    $"{server.Name}'s identity has changed since it was added, so ADA won't connect. If the server was reinstalled, remove it and add it again.",
                SshAuthenticationException => $"{server.Name} refused the login. Check the user name and password or key.",
                _ => $"Couldn't reach {server.Address}:{server.Port}: {e.Message}",
            }, e);
        }
    }

    /// <exception cref="ServerConnectException">The key file is missing, unreadable or needs its passphrase.</exception>
    private static AuthenticationMethod Login(ServerConnection server, string? secret)
    {
        if (server.Auth == ServerAuth.Password)
        {
            return new PasswordAuthenticationMethod(server.User, secret);
        }
        try
        {
            var key = secret is null ? new PrivateKeyFile(server.PrivateKeyPath!) : new PrivateKeyFile(server.PrivateKeyPath!, secret);
            return new PrivateKeyAuthenticationMethod(server.User, key);
        }
        catch (SshPassPhraseNullOrEmptyException e)
        {
            throw new ServerConnectException("The key file needs its passphrase.", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SshException)
        {
            throw new ServerConnectException($"Couldn't read the key file {server.PrivateKeyPath}: {e.Message}", e);
        }
    }

    private void OnConnectionError(object? sender, ExceptionEventArgs e)
    {
        if (Server is { } server && State == ServerState.Connected)
        {
            _log.LogWarning(e.Exception, "Lost the connection to {Server}", server.Name);
            SetState(server, ServerState.Unavailable, $"Lost the connection to {server.Name}.", Install);
            ScheduleRetry(server);
        }
    }

    private void ScheduleRetry(ServerConnection server)
    {
        var retry = _retry = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delay in RETRY_DELAYS.Concat(Enumerable.Repeat(RETRY_DELAYS[^1], int.MaxValue)))
                {
                    await Task.Delay(delay, retry.Token);
                    await ConnectCoreAsync(server, explicitConnect: false, fromRetry: true, retry.Token);
                    if (State == ServerState.Connected)
                    {
                        return;
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                // Disconnected, connected some other way, or shutting down.
            }
        });
    }

    private void Close()
    {
        if (_client is { } client)
        {
            client.ErrorOccurred -= OnConnectionError;
            client.Dispose();
            _client = null;
        }
    }

    private void SetState(ServerConnection? server, ServerState state, string? problem, GameInstall? install)
    {
        (Server, State, Problem, Install) = (server, state, problem, install);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Matches(GameInstall game, ServerConnection server) => game.GamePath == InstallKey(server.Id);

    private static GameInstall ToInstall(ServerConnection server, GameBuild? build) => new(
        InstallKey(server.Id), GameLauncher.Unknown, server.Host, GameKind.DedicatedServer, null, server.Name, build);
}
