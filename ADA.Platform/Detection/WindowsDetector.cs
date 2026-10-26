using System.Runtime.Versioning;
using Microsoft.Win32;
using ADA.Core.Games;
using ADA.Platform.Launchers;

namespace ADA.Platform.Detection;

/// <summary>Windows: finds Steam and Epic through their registry keys.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDetector : IGameDetector
{
    public IReadOnlyList<GameInstall> Detect()
    {
        var results = new List<GameInstall>();

        var steamRoot = ReadString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath")
                        ?? ReadString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (steamRoot is not null)
            results.AddRange(new SteamLibraryScanner(MapNative, GameHost.Windows, "Steam").Scan(steamRoot));

        var epicData = ReadString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                           "Epic", "EpicGamesLauncher", "Data");
        results.AddRange(new EpicManifestScanner(MapNative, GameHost.Windows, "Epic Games Launcher")
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
