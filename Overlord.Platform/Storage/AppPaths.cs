namespace Overlord.Platform.Storage;

/// <summary>
/// Where Overlord keeps its own data. One root per OS, with fixed folders under it:
/// <code>
/// Windows  %LOCALAPPDATA%\Overlord
/// Linux    ~/.overlord
/// macOS    ~/Library/Application Support/Overlord
///   Databases/   SQLite files (mod catalogue)
///   Config/      settings
///   Staging/     downloaded archives (Staging/Archives)
///   Logs/        daily log files, the last week kept
/// </code>
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "Overlord";

    public string Root { get; }

    public string Databases => Path.Combine(Root, "Databases");

    public string Config => Path.Combine(Root, "Config");

    public string Staging => Path.Combine(Root, "Staging");

    public string Archives => Path.Combine(Staging, "Archives");

    public string Logs => Path.Combine(Root, "Logs");

    public string CatalogDatabase => Path.Combine(Databases, "catalog.db");

    public string SettingsFile => Path.Combine(Config, "settings.json");

    /// <param name="root">Overrides the OS default; for tests only.</param>
    public AppPaths(string root) => Root = root;

    public static AppPaths ForCurrentOS() => new(DefaultRoot());

    /// <summary>Creates the root and its folders if missing; safe to call every launch.</summary>
    public AppPaths EnsureCreated()
    {
        Directory.CreateDirectory(Databases);
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Archives);
        Directory.CreateDirectory(Logs);
        return this;
    }

    private static string DefaultRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", AppFolderName);
        if (OperatingSystem.IsLinux())
            return Path.Combine(home, "." + AppFolderName.ToLowerInvariant());

        throw new PlatformNotSupportedException("Overlord supports Windows, Linux and macOS.");
    }
}
