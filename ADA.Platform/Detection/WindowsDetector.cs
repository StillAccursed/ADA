using System.Runtime.Versioning;
using ADA.Core.Games;
using ADA.Platform.Launchers;
using ADA.Platform.Paths;
using Microsoft.Win32;

namespace ADA.Platform.Detection;

/// <summary>Windows: finds Steam and Epic through their registry keys.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDetector : IGameDetector
{
    /// <inheritdoc />
    public IReadOnlyList<GameInstall> Detect()
    {
        var results = new List<GameInstall>();
        // Every install, from either store and of either kind, shares the signed-in user's folder.
        var userData = GameUserDataPaths.ForWindows();

        var steamRoot = ReadString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? ReadString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (steamRoot is not null)
        {
            results.AddRange(new SteamLibraryScanner(MapNative, GameHost.Windows, "Steam", (_, _) => userData)
                .Scan(steamRoot));
        }

        var epicData = ReadString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                           "Epic", "EpicGamesLauncher", "Data");
        results.AddRange(new EpicManifestScanner(MapNative, GameHost.Windows, "Epic Games Launcher", _ => userData)
            .Scan(Path.Combine(epicData, "Manifests")));

        return results;
    }

    private static string? MapNative(string path) => Directory.Exists(path) ? path : null;

    private static string? ReadString(RegistryKey hive, string key, string name)
    {
        try
        {
            using var k = hive.OpenSubKey(key);
            return k?.GetValue(name) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
