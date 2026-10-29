using ADA.Core.Games;

namespace ADA.Core.Servers;

public enum ServerAuth
{
    Password,

    /// <summary>An SSH private key file, optionally protected by a passphrase.</summary>
    PrivateKey,
}

/// <summary>
/// A saved dedicated server. Holds no secrets: the password or key passphrase is in the OS
/// secret store, under the server's <see cref="Id"/>.
/// </summary>
/// <param name="Id">Stable identity: the secret's key, and part of the server's install key.</param>
/// <param name="Name">What the user called it, shown everywhere the server appears.</param>
/// <param name="GameRoot">
/// Absolute path on the server of the folder containing <c>FactoryServer.sh</c> or
/// <c>FactoryServer.exe</c> (and <c>FactoryGame</c>).
/// </param>
/// <param name="Host">The server's OS: <see cref="GameHost.Linux"/> or <see cref="GameHost.Windows"/>. Picks the mod build to install.</param>
/// <param name="HostKeyFingerprint">SHA-256 of the SSH host key accepted at setup. A different key refuses to connect.</param>
/// <param name="PrivateKeyPath">Path on this PC of the key file, when <paramref name="Auth"/> is <see cref="ServerAuth.PrivateKey"/>.</param>
/// <param name="ApiPort">The server API port, used to ask whether the server is running.</param>
/// <param name="Connected">The user left it connected; it reconnects on launch.</param>
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
    bool Connected = false)
{
    public const int DEFAULT_SSH_PORT = 22;
}
