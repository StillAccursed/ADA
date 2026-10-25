using System.Text.Json;
using Overlord.Core.Games;
using Overlord.Platform.Launchers;

namespace Overlord.Platform.Detection;

/// <summary>
/// Linux: Steam (native, Flatpak, Snap) libraries, where Proton keeps the game files, and Epic
/// installs managed by Heroic or Legendary.
/// </summary>
public sealed class LinuxDetector(string home) : IGameDetector
{
    public LinuxDetector() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    public IReadOnlyList<GameInstall> Detect()
    {
        var results = new List<GameInstall>();

        string[] steamRoots =
        [
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
        ];
        // ~/.steam/steam is usually a symlink to ~/.local/share/Steam; scan each real root once.
        var realRoots = steamRoots
            .Where(Directory.Exists)
            .DistinctBy(r => new DirectoryInfo(r).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? r);
        foreach (var root in realRoots)
            results.AddRange(new SteamLibraryScanner(MapNative, GameHost.LinuxCompat, "Steam").Scan(root));

        string[] legendaryInstalled =
        [
            Path.Combine(home, ".config", "heroic", "legendaryConfig", "legendary", "installed.json"),
            Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic", "legendaryConfig", "legendary", "installed.json"),
            Path.Combine(home, ".config", "legendary", "installed.json"),
        ];
        foreach (var file in legendaryInstalled)
            results.AddRange(ScanLegendary(file));

        // Two roots can still list the same extra library.
        return results.DistinctBy(r => r.GamePath).ToList();
    }

    private static IEnumerable<GameInstall> ScanLegendary(string installedJson)
    {
        if (!File.Exists(installedJson))
            yield break;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(installedJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                yield break;

            foreach (var game in doc.RootElement.EnumerateObject())
            {
                // Legendary keys games by Epic app name; Satisfactory's start with its codename "Crab".
                if (!game.Name.StartsWith("Crab", StringComparison.OrdinalIgnoreCase))
                    continue;

                var path = game.Value.TryGetProperty("install_path", out var p) ? p.GetString() : null;
                if (path is null || GameInstallValidator.InspectKind(path) is not { } kind)
                    continue;

                var version = game.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
                yield return new GameInstall(path, GameLauncher.Epic, GameHost.LinuxCompat, kind, version, "Heroic / Legendary");
            }
        }
    }

    private static string? MapNative(string path) => Directory.Exists(path) ? path : null;
}
