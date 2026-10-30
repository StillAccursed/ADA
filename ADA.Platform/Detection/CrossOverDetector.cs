using ADA.Core.Games;
using ADA.Platform.Formats;
using ADA.Platform.Launchers;
using ADA.Platform.Paths;

namespace ADA.Platform.Detection;

/// <summary>
/// macOS: looks inside every CrossOver bottle for the Windows Steam and Epic launchers and the games
/// they have recorded.
/// </summary>
internal sealed class CrossOverDetector(string bottlesRoot) : IGameDetector
{
    public static string DefaultBottlesRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "CrossOver", "Bottles");

    public CrossOverDetector() : this(DefaultBottlesRoot)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<GameInstall> Detect()
    {
        if (!Directory.Exists(bottlesRoot))
        {
            return [];
        }

        var results = new List<GameInstall>();
        foreach (var bottle in Directory.EnumerateDirectories(bottlesRoot))
        {
            var prefix = new WinePrefix(bottle);
            if (!File.Exists(prefix.SystemRegistry))
            {
                continue;
            }

            var location = $"CrossOver bottle \"{Path.GetFileName(bottle)}\"";
            // Every install in a bottle runs as the bottle's one Windows user.
            var userData = GameUserDataPaths.InWinePrefix(prefix, GameUserDataPaths.CROSSOVER_USER);
            results.AddRange(DetectEpic(prefix, location, userData));
            results.AddRange(DetectSteam(prefix, location, userData));
        }
        return results;
    }

    private static IEnumerable<GameInstall> DetectEpic(WinePrefix prefix, string location, string userData)
    {
        var appData = ReadFirst(prefix.SystemRegistry, "AppDataPath",
                          @"Software\Wow6432Node\Epic Games\EpicGamesLauncher",
                          @"Software\Epic Games\EpicGamesLauncher")
                      ?? @"C:\ProgramData\Epic\EpicGamesLauncher\Data";

        var manifests = prefix.ToHostPath(appData.TrimEnd('\\') + @"\Manifests");
        return manifests is null
            ? []
            : new EpicManifestScanner(prefix.ToHostPath, GameHost.CrossOver, location, _ => userData).Scan(manifests);
    }

    private static IEnumerable<GameInstall> DetectSteam(WinePrefix prefix, string location, string userData)
    {
        var steamRoot = ReadFirst(prefix.SystemRegistry, "InstallPath",
                            @"Software\Wow6432Node\Valve\Steam",
                            @"Software\Valve\Steam")
                        ?? WineRegistryFile.ReadString(prefix.UserRegistry, @"Software\Valve\Steam", "SteamPath")
                        ?? @"C:\Program Files (x86)\Steam";

        var host = prefix.ToHostPath(steamRoot);
        return host is null
            ? []
            : new SteamLibraryScanner(prefix.ToHostPath, GameHost.CrossOver, location, (_, _) => userData).Scan(host);
    }

    private static string? ReadFirst(string regFile, string valueName, params string[] keys) =>
        keys.Select(k => WineRegistryFile.ReadString(regFile, k, valueName)).FirstOrDefault(v => v is not null);
}
