using ADA.Core.Games;

namespace ADA.Core.Servers;

public enum ServerAuth
{
    Password,

    /// <summary>An SSH private key file, optionally protected by a passphrase.</summary>
    PrivateKey,
}

/// <summary>How ADA reaches a saved server's files.</summary>
public enum ServerTransport
{
    /// <summary>Over SSH, with an address, user and password or key.</summary>
    Sftp,

    /// <summary>
    /// FTP with explicit TLS (FTPS), with an address, user and password. Plain FTP is refused: it
    /// would send the password unencrypted.
    /// </summary>
    Ftp,

    /// <summary>
    /// A folder this PC can open: a local install, or a network share the OS has mounted. No login;
    /// the address is only used, if given, to ask the server API whether it's running.
    /// </summary>
    Folder,
}

/// <summary>
/// A saved dedicated server. Holds no secrets: the password or key passphrase is in the OS
/// secret store, under the server's <see cref="Id"/>.
/// </summary>
/// <param name="Id">Stable identity: the secret's key, and part of the server's install key.</param>
/// <param name="Name">What the user called it, shown everywhere the server appears.</param>
/// <param name="Address">The SSH host; for a folder server, where the server API answers, or empty when unknown.</param>
/// <param name="GameRoot">
/// Absolute path of the folder containing <c>FactoryServer.sh</c> or <c>FactoryServer.exe</c> (and
/// <c>FactoryGame</c>): on the server for SFTP, or on this PC for a folder server.
/// </param>
/// <param name="Host">The server's OS: <see cref="GameHost.Linux"/> or <see cref="GameHost.Windows"/>. Picks the mod build to install.</param>
/// <param name="HostKeyFingerprint">
/// SHA-256 of the SSH host key, or for FTP of the TLS certificate, accepted at setup. A different one refuses to connect.
/// </param>
/// <param name="PrivateKeyPath">Path on this PC of the key file, when <paramref name="Auth"/> is <see cref="ServerAuth.PrivateKey"/>.</param>
/// <param name="ApiPort">The server API port, used to ask whether the server is running.</param>
/// <param name="Connected">The user left it connected; it reconnects on launch.</param>
/// <param name="Transport">
/// How its files are reached. For <see cref="ServerTransport.Folder"/>, <paramref name="Port"/>,
/// <paramref name="User"/>, <paramref name="Auth"/>, <paramref name="HostKeyFingerprint"/> and
/// <paramref name="PrivateKeyPath"/> are unused.
/// </param>
public sealed record ServerConnection(
    Guid Id,
    string Name,
    string Address,
    int Port,
    string User,
    ServerAuth Auth,
    string GameRoot,
    GameHost Host,
    string HostKeyFingerprint,
    string? PrivateKeyPath = null,
    int ApiPort = 7777,
    bool Connected = false,
    ServerTransport Transport = ServerTransport.Sftp)
{
    public const int DEFAULT_SSH_PORT = 22;
    public const int DEFAULT_FTP_PORT = 21;
}
