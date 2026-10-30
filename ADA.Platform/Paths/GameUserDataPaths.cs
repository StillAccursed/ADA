using ADA.Platform.Launchers;

namespace ADA.Platform.Paths;

/// <summary>
/// Where an install keeps its per-user folder (saves, blueprints, ini config), for each way the game
/// can run. Each method gives where the folder is or will be without requiring it to exist, so an
/// install's path doesn't change when the game first creates it.
/// </summary>
internal static class GameUserDataPaths
{
    /// <summary>The Windows user CrossOver creates in every bottle.</summary>
    public const string CROSSOVER_USER = "crossover";

    /// <summary>The Windows user Proton creates in every prefix.</summary>
    public const string PROTON_USER = "steamuser";

    private const string FOLDER_NAME = "FactoryGame";

    public static string ForWindows() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FOLDER_NAME);

    public static string InWinePrefix(WinePrefix prefix, string defaultUser) =>
        Path.Combine(prefix.LocalAppData(defaultUser), FOLDER_NAME);

    /// <summary>The native Linux dedicated server's folder, for the account that runs it.</summary>
    public static string ForLinuxServer(string home) => Path.Combine(home, ".config", "Epic", FOLDER_NAME);

    /// <summary>
    /// The folder inside the Proton prefix of the client installed in <paramref name="library"/>.
    /// Current Steam keeps the prefix in the game's library and older Steam in its root library. The first
    /// that exists wins, and the game's library when neither does yet (Proton creates it on first launch).
    /// </summary>
    public static string ForProton(string library, string steamRoot)
    {
        var prefix = new[] { library, steamRoot }.Select(ProtonPrefix).FirstOrDefault(Directory.Exists)
                     ?? ProtonPrefix(library);
        return InWinePrefix(new WinePrefix(prefix), PROTON_USER);
    }

    private static string ProtonPrefix(string library) =>
        Path.Combine(library, "steamapps", "compatdata", SteamLibraryScanner.CLIENT_APP_ID, "pfx");
}
