namespace ADA.Core.Games;

/// <summary>Which store the install was bought from, as recorded by that store's own launcher.</summary>
public enum GameLauncher
{
    Steam,
    Epic,

    /// <summary>A server reached over SFTP: which store it came from can't be read remotely.</summary>
    Unknown,
}

/// <summary>How the Windows game is being run on this machine.</summary>
public enum GameHost
{
    /// <summary>Natively on Windows.</summary>
    Windows,

    /// <summary>On Linux through Steam Proton or Heroic/Wine.</summary>
    LinuxCompat,

    /// <summary>Natively on Linux (the dedicated server has a Linux build).</summary>
    Linux,

    /// <summary>On macOS inside a CrossOver bottle.</summary>
    CrossOver,
}

public enum GameKind
{
    Client,
    DedicatedServer,
}

/// <summary>
/// A Satisfactory install: a local one found through a launcher's own records, or a dedicated
/// server the user added and that passed Test Connection. A local install is deliberately never
/// built from a user-chosen folder; see <see cref="GameInstallValidator"/>.
/// </summary>
/// <param name="GamePath">
/// Host filesystem path of the game root (the folder that contains <c>FactoryGame</c>); for a server,
/// its key instead (see <see cref="IsRemote"/>).
/// </param>
/// <param name="Launcher">Store that owns the install.</param>
/// <param name="Host">How the game runs on this OS.</param>
/// <param name="Kind">Client or dedicated server.</param>
/// <param name="Version">
/// Build identifier as reported by the launcher, if it gave one. Steam's is its own build counter,
/// not the game's changelist; use <see cref="Build"/> for compatibility checks.
/// </param>
/// <param name="Location">Human-readable origin, e.g. the CrossOver bottle name or Steam library.</param>
/// <param name="Build">The game build read from the install's files, or null if it could not be read.</param>
public sealed record GameInstall(
    string GamePath,
    GameLauncher Launcher,
    GameHost Host,
    GameKind Kind,
    string? Version,
    string Location,
    GameBuild? Build = null)
{
    /// <summary>Install keys for servers start with this; see <see cref="IsRemote"/>.</summary>
    public const string REMOTE_SCHEME = "sftp://";

    public string ModsPath => Path.Combine(GamePath, "FactoryGame", "Mods");

    /// <summary>
    /// A server reached over SFTP. Its <see cref="GamePath"/> is a key (<c>sftp://{server id}</c>),
    /// not a path on this machine, so it must never be opened as a folder.
    /// </summary>
    public bool IsRemote => GamePath.StartsWith(REMOTE_SCHEME, StringComparison.Ordinal);

    public string DisplayName => (Kind, Launcher) switch
    {
        // Location is the name the user gave the server.
        (_, GameLauncher.Unknown) => $"{Location} (Dedicated Server)",
        (GameKind.DedicatedServer, _) => $"Satisfactory Dedicated Server ({Launcher})",
        _ => $"Satisfactory ({Launcher})",
    };

    public string BuildText => Build is null
        ? "Game build unknown"
        : $"Game {Build.GameVersion ?? "?"} · CL {Build.Changelist}";
}
