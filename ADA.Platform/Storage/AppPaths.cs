namespace ADA.Platform.Storage;

/// <summary>
/// Where ADA keeps its own data. One root per OS, with fixed folders under it:
/// <code>
/// Windows  %LOCALAPPDATA%\ADA
/// Linux    ~/.ada
/// macOS    ~/Library/Application Support/ADA
///   Databases/   SQLite files (mod catalogue)
///   Config/      settings, profiles (Config/Profiles/*.olp), each game's active profile and saved servers
///   Staging/     downloaded archives (Staging/Archives), and restored ADA config waiting for the next start (Staging/PendingConfig)
///   Backups/     backups of saves, blueprints and configs, one .zip each
///   Logs/        daily log files, the last week kept
/// </code>
/// </summary>
public sealed class AppPaths
{
    private const string APP_FOLDER_NAME = "ADA";

    internal string Root { get; }

    internal string Databases => Path.Combine(Root, "Databases");

    public string Config => Path.Combine(Root, "Config");

    public string Staging => Path.Combine(Root, "Staging");

    public string Archives => Path.Combine(Staging, "Archives");

    public string Logs => Path.Combine(Root, "Logs");

    public string CatalogDatabase => Path.Combine(Databases, "catalog.db");

    public string SettingsFile => Path.Combine(Config, "settings.json");

    public string Profiles => Path.Combine(Config, "Profiles");

    /// <summary>Mods the user starred on the Mods page.</summary>
    public string FavouritesFile => Path.Combine(Config, "favourites.json");

    /// <summary>Which profile each game uses.</summary>
    public string ProfilesState => Path.Combine(Config, "profiles.json");

    /// <summary>Saved dedicated servers (no secrets; those are in the OS secret store).</summary>
    public string ServersFile => Path.Combine(Config, "servers.json");

    /// <summary>Backups of saves, blueprints and configs, one <c>.zip</c> each.</summary>
    public string Backups => Path.Combine(Root, "Backups");

    /// <summary>ADA config restored from a backup, moved into <see cref="Config"/> at the next start.</summary>
    public string PendingAdaConfig => Path.Combine(Staging, "PendingConfig");

    /// <param name="root">Overrides the OS default; for tests only.</param>
    public AppPaths(string root) => Root = root;

    /// <summary>Uses the current OS data root without creating any directories.</summary>
    public static AppPaths ForCurrentOS() => new(DefaultRoot());

    /// <summary>Creates the root and its folders if missing; safe to call every launch.</summary>
    public AppPaths EnsureCreated()
    {
        Directory.CreateDirectory(Databases);
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Archives);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
        return this;
    }

    private static string DefaultRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), APP_FOLDER_NAME);
        }
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", APP_FOLDER_NAME);
        }
        if (OperatingSystem.IsLinux())
        {
            return Path.Combine(home, "." + APP_FOLDER_NAME.ToLowerInvariant());
        }

        throw new PlatformNotSupportedException("ADA supports Windows, Linux and macOS.");
    }
}
