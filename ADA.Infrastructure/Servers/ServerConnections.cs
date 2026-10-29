using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using FluentFTP;
using FluentFTP.Exceptions;
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
/// <param name="needsSecret">No password is saved, or the key needs its passphrase: asking the user can fix it.</param>
public sealed class ServerConnectException(string message, Exception? inner = null, bool needsSecret = false)
    : Exception(message, inner)
{
    public bool NeedsSecret { get; } = needsSecret;
}

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
    private FluentFtpSession? _ftp;
    private CancellationTokenSource? _reachability;

    // 1 while a reachability probe runs, so a hung share never piles up probes.
    private int _checking;
    private CancellationTokenSource? _retry;

    public event EventHandler? Changed;

    /// <summary>How often a connected folder or FTP server is checked; tests shorten it.</summary>
    internal TimeSpan ReachabilityInterval { get; init; } = TimeSpan.FromSeconds(15);

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

    /// <summary>
    /// Whether the server behind <paramref name="game"/> answers its API; false when it doesn't, or
    /// when a folder server was saved without an address, so there's nowhere to ask.
    /// </summary>
    public Task<bool> IsRunningAsync(GameInstall game, CancellationToken ct = default) =>
        Server is { } server && Matches(game, server) && !string.IsNullOrWhiteSpace(server.Address)
            ? api.IsRunningAsync(server.Address, server.ApiPort, ct)
            : Task.FromResult(false);

    /// <summary>
    /// For a connected folder server, checks its folder is still there (a network share can be
    /// unmounted at any time); for FTP, that the connection still answers. If not, marks the server
    /// unavailable and retries in the background. Runs every <see cref="ReachabilityInterval"/> by
    /// itself once such a server connects; SFTP notices a dropped connection without it.
    /// </summary>
    /// <remarks>
    /// The probe runs outside the connection gate, so a hung share can't hold up Connect or
    /// Disconnect, and an FTP connection busy with a transfer counts as alive. The result is only
    /// applied if the gate is free and the same connection is still current; otherwise this round is
    /// skipped. A probe still running from the last round makes this one a no-op.
    /// </remarks>
    public async Task CheckReachableAsync(CancellationToken ct = default)
    {
        if (Server is not { } server || State != ServerState.Connected || server.Transport == ServerTransport.Sftp
            || Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }
        try
        {
            var session = _ftp;
            var problem = await Task.Run(() => server.Transport == ServerTransport.Folder
                ? Directory.Exists(FactoryGame(server)) ? null : FolderMissing(server)
                : session?.TryNoop() == true ? null : $"Lost the connection to {server.Name}.", ct);
            if (problem is null || !await _gate.WaitAsync(0, ct))
            {
                return;
            }
            try
            {
                if (ReferenceEquals(Server, server) && State == ServerState.Connected && ReferenceEquals(_ftp, session))
                {
                    _log.LogWarning("{Server} is no longer reachable: {Problem}", server.Name, problem);
                    SetState(server, ServerState.Unavailable, problem, Install);
                    ScheduleRetry(server);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>Free space where the connected server's game is; null when it's not connected or can't tell.</summary>
    public Task<long?> AvailableFreeSpaceAsync(GameInstall game, CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            return FilesFor(game) switch
            {
                SftpGameFiles files => files.AvailableFreeSpace(),
                FolderGameFiles files => files.AvailableFreeSpace(),
                _ => null,
            };
        }
        catch (ServerUnavailableException)
        {
            return null;
        }
    }, ct);

    /// <summary>Who holds the connected server's lock, if anyone other than this ADA; null when it's free, unreadable or not connected.</summary>
    public Task<LockHolder?> LockHolderAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        // Holder reads swallow I/O failures, so an unreadable lock reads as free here.
        return LockFileFor() is { } file ? RemoteLock.HeldByOther(file) : null;
    }, ct);

    /// <summary>
    /// Removes the connected server's lock after the user has confirmed its holder stopped. Recovery
    /// runs on the next operation, as after a crash.
    /// </summary>
    /// <exception cref="ServerUnavailableException">The server isn't connected, or removing the lock failed.</exception>
    public Task BreakLockAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (Server is not { } server || LockFileFor() is not { } file)
        {
            throw new ServerUnavailableException("Connect to the server first.");
        }
        try
        {
            RemoteLock.Break(file);
            _log.LogWarning("Took over {Server}'s lock at the user's request", server.Name);
        }
        catch (IOException e)
        {
            throw new ServerUnavailableException($"Couldn't remove the lock: {e.Message}", e);
        }
    }, ct);

    /// <summary>The connected server's files.</summary>
    /// <exception cref="ServerUnavailableException"><paramref name="game"/> isn't the connected server, or it's unreachable.</exception>
    internal IGameFiles FilesFor(GameInstall game)
    {
        if (Server is not { } server || !Matches(game, server))
        {
            throw new ServerUnavailableException("Connect to this server on the Servers page first.");
        }
        if (State != ServerState.Connected)
        {
            throw new ServerUnavailableException(Problem ?? $"{server.Name} isn't connected right now.");
        }
        if (server.Transport == ServerTransport.Folder)
        {
            return new FolderGameFiles(FactoryGame(server), _log);
        }
        if (server.Transport == ServerTransport.Ftp)
        {
            return _ftp is { } ftp
                ? new FtpGameFiles(ftp, server.GameRoot, _log)
                : throw new ServerUnavailableException(Problem ?? $"{server.Name} isn't connected right now.");
        }
        if (_client is not { IsConnected: true } client)
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
                GameBuild? build;
                if (server.Transport == ServerTransport.Folder)
                {
                    build = await OpenFolderAsync(server, ct);
                }
                else if (server.Transport == ServerTransport.Ftp)
                {
                    var ftp = await OpenFtpAsync(server, ct);
                    try
                    {
                        build = await ReadBuildAsync(new FtpServerFolders(ftp), server, ct);
                    }
                    catch
                    {
                        // Not stored yet, so nothing else would close it.
                        ftp.Dispose();
                        throw;
                    }
                    _ftp = ftp;
                }
                else
                {
                    var client = await OpenAsync(server, ct);
                    try
                    {
                        build = await ReadBuildAsync(new SftpServerFolders(client), server, ct);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }
                    _client = client;
                    _client.ErrorOccurred += OnConnectionError;
                }
                store.SetConnected(server.Id);
                SetState(server, ServerState.Connected, null, ToInstall(server, build));
                if (server.Transport != ServerTransport.Sftp)
                {
                    StartReachabilityChecks();
                }
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
            throw new ServerConnectException($"Enter the password for {server.Name} to connect.", needsSecret: true);
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

    /// <summary>
    /// "Connects" a folder server: checks its folder is there, then reads its build. There's no
    /// connection to keep open; <see cref="CheckReachableAsync"/> notices if the folder goes away.
    /// </summary>
    /// <exception cref="ServerConnectException">The folder isn't there, e.g. its share isn't mounted.</exception>
    private async Task<GameBuild?> OpenFolderAsync(ServerConnection server, CancellationToken ct)
    {
        if (!await Task.Run(() => Directory.Exists(FactoryGame(server)), ct))
        {
            throw new ServerConnectException(FolderMissing(server));
        }
        return await ReadBuildAsync(new LocalServerFolders(), server, ct);
    }

    private static string FolderMissing(ServerConnection server) =>
        $"{server.Name}'s folder isn't available. If it's on a network share, check the share is mounted.";

    private static string FactoryGame(ServerConnection server) => Path.Combine(server.GameRoot, "FactoryGame");

    /// <summary>The connected server's lock file; null when no server is connected.</summary>
    private ILockFile? LockFileFor() => Server switch
    {
        { Transport: ServerTransport.Folder } folder when State == ServerState.Connected =>
            FolderGameFiles.LockFile(FactoryGame(folder), GameModFolders.LOCK),
        { Transport: ServerTransport.Ftp } ftp when State == ServerState.Connected && _ftp is { } session =>
            new FtpLockFile(session, LockPath(ftp)),
        { Transport: ServerTransport.Sftp } server when _client is { IsConnected: true } client => new SftpLockFile(client, LockPath(server)),
        _ => null,
    };

    /// <summary>
    /// Opens an FTPS connection: explicit TLS only, so a server offering only plain FTP is refused
    /// rather than sent the password unencrypted. The certificate is pinned like an SSH host key: an
    /// empty saved fingerprint trusts the first one seen (only the setup check passes one).
    /// </summary>
    /// <exception cref="ServerConnectException">Unreachable, unencrypted, refused the login, or its certificate changed.</exception>
    internal async Task<FluentFtpSession> OpenFtpAsync(ServerConnection server, CancellationToken ct, string? secret = null,
        Action<string>? certificateSeen = null)
    {
        secret ??= await secrets.LoadAsync(server.Id, ct);
        if (secret is null)
        {
            throw new ServerConnectException($"Enter the password for {server.Name} to connect.", needsSecret: true);
        }

        var client = new FtpClient(server.Address, server.User, secret, server.Port, new FtpConfig
        {
            EncryptionMode = FtpEncryptionMode.Explicit,
            DataConnectionEncryption = true,
            ValidateAnyCertificate = false,
            ConnectTimeout = (int)CONNECT_TIMEOUT.TotalMilliseconds,
        });
        var identityChanged = false;
        client.ValidateCertificate += (_, e) =>
        {
            var fingerprint = CertificateFingerprint(e.Certificate);
            certificateSeen?.Invoke(fingerprint);
            e.Accept = server.HostKeyFingerprint.Length == 0 || fingerprint == server.HostKeyFingerprint;
            identityChanged |= !e.Accept;
        };

        try
        {
            await Task.Run(client.Connect, ct);
            return new FluentFtpSession(client);
        }
        catch (Exception e) when (e is FtpException or AuthenticationException or IOException or TimeoutException
                                      or System.Net.Sockets.SocketException)
        {
            client.Dispose();
            throw new ServerConnectException(FtpConnectProblem(server, e, identityChanged), e);
        }
    }

    /// <summary>What to tell the user when an FTPS connection fails.</summary>
    internal static string FtpConnectProblem(ServerConnection server, Exception e, bool identityChanged) => e switch
    {
        _ when identityChanged =>
            $"{server.Name}'s certificate has changed since it was added, so ADA won't connect. If the host renewed it, remove the server and add it again.",
        FtpSecurityNotAvailableException =>
            "This server only offers unencrypted FTP, which would send your password in plain text. Use SFTP if the host offers it.",
        FtpAuthenticationException => $"{server.Name} refused the login. Check the user name and password.",
        _ => $"Couldn't reach {server.Address}:{server.Port}: {e.Message}",
    };

    /// <summary>SHA-256 of the certificate, base64 without padding, the same form as an SSH host key fingerprint.</summary>
    internal static string CertificateFingerprint(X509Certificate certificate) =>
        Convert.ToBase64String(SHA256.HashData(certificate.GetRawCertData())).TrimEnd('=');

    /// <summary>The server's game build, or null if it can't be read; a missing build doesn't stop the connection.</summary>
    private async Task<GameBuild?> ReadBuildAsync(IServerFolders folders, ServerConnection server, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => ServerSetupCheck.ReadBuild(folders, server.GameRoot), ct);
        }
        catch (IOException e)
        {
            _log.LogWarning(e, "Couldn't read {Server}'s game build", server.Name);
            return null;
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
            throw new ServerConnectException("The key file needs its passphrase.", e, needsSecret: true);
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

    /// <summary>Checks the current folder or FTP server every <see cref="ReachabilityInterval"/> until <see cref="Close"/>.</summary>
    private void StartReachabilityChecks()
    {
        var watch = _reachability = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(ReachabilityInterval, watch.Token);
                    await CheckReachableAsync(watch.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                // Disconnected, reconnecting, or shutting down.
            }
        });
    }

    private void Close()
    {
        _reachability?.Cancel();
        _reachability = null;
        if (_client is { } client)
        {
            client.ErrorOccurred -= OnConnectionError;
            client.Dispose();
            _client = null;
        }
        _ftp?.Dispose();
        _ftp = null;
    }

    private void SetState(ServerConnection? server, ServerState state, string? problem, GameInstall? install)
    {
        (Server, State, Problem, Install) = (server, state, problem, install);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool Matches(GameInstall game, ServerConnection server) => game.GamePath == InstallKey(server.Id);

    private static string LockPath(ServerConnection server) => $"{server.GameRoot.TrimEnd('/')}/FactoryGame/{GameModFolders.LOCK}";

    /// <summary>A saved server as an install: the same key and display name whether or not it's connected.</summary>
    public static GameInstall ToInstall(ServerConnection server, GameBuild? build) => new(
        InstallKey(server.Id), GameLauncher.Unknown, server.Host, GameKind.DedicatedServer, null, server.Name, build);
}
