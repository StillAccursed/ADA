using System.Text.Json;
using ADA.Core.Games;
using ADA.Platform.Formats;
using ADA.Platform.Launchers;
using ADA.Platform.Paths;

namespace ADA.Platform.Detection;

/// <summary>
/// Linux: Steam (native, Flatpak, Snap) libraries, where Proton keeps the game files, and Epic
/// installs managed by Heroic or Legendary.
/// </summary>
internal sealed class LinuxDetector(string home) : IGameDetector
{
    /// <summary>Looks under the current user's home folder.</summary>
    public LinuxDetector() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <inheritdoc />
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
        {
            var scanner = new SteamLibraryScanner(MapNative, GameHost.LinuxCompat, "Steam",
                (library, kind) => kind == GameKind.Client
                    ? GameUserDataPaths.ForProton(library, root)
                    : GameUserDataPaths.ForLinuxServer(home));
            results.AddRange(scanner.Scan(root));
        }

        var heroic = Path.Combine(home, ".config", "heroic");
        var heroicFlatpak = Path.Combine(home, ".var", "app", "com.heroicgameslauncher.hgl", "config", "heroic");
        (string InstalledJson, string? HeroicRoot)[] legendaryInstalled =
        [
            (Path.Combine(heroic, "legendaryConfig", "legendary", "installed.json"), heroic),
            (Path.Combine(heroicFlatpak, "legendaryConfig", "legendary", "installed.json"), heroicFlatpak),
            (Path.Combine(home, ".config", "legendary", "installed.json"), null),
        ];
        foreach (var (file, heroicRoot) in legendaryInstalled)
        {
            results.AddRange(ScanLegendary(file, heroicRoot));
        }

        // Two roots can still list the same extra library.
        return results.DistinctBy(r => r.GamePath).ToList();
    }

    /// <param name="heroicRoot">Heroic's config folder when Heroic manages this Legendary; null for Legendary alone.</param>
    private static IEnumerable<GameInstall> ScanLegendary(string installedJson, string? heroicRoot)
    {
        if (!File.Exists(installedJson))
        {
            yield break;
        }

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
            {
                yield break;
            }

            foreach (var game in doc.RootElement.EnumerateObject())
            {
                // Legendary keys games by Epic app name; Satisfactory's start with its codename "Crab".
                if (!game.Name.StartsWith("Crab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = game.Value.TryGetProperty("install_path", out var p) ? p.GetString() : null;
                if (path is null || GameInstallValidator.InspectKind(path) is not { } kind)
                {
                    continue;
                }

                var version = game.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
                var userData = heroicRoot is null ? null : HeroicUserData(heroicRoot, game.Name);
                yield return new GameInstall(path, GameLauncher.Epic, GameHost.LinuxCompat, kind, version, "Heroic / Legendary",
                    GameBuildReader.Read(path), userData);
            }
        }
    }

    /// <summary>
    /// The user-data folder inside the Wine prefix Heroic recorded for the game in
    /// <c>GamesConfig/{appName}.json</c>; null when there's no readable config. A Proton prefix keeps the
    /// Windows drive in <c>pfx</c> under the configured folder.
    /// </summary>
    private static string? HeroicUserData(string heroicRoot, string appName)
    {
        string? winePrefix;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(heroicRoot, "GamesConfig", $"{appName}.json")));
            winePrefix = doc.RootElement.ValueKind == JsonValueKind.Object
                         && doc.RootElement.TryGetProperty(appName, out var game)
                         && game.ValueKind == JsonValueKind.Object
                         && game.TryGetProperty("winePrefix", out var prefix)
                         && prefix.ValueKind == JsonValueKind.String
                ? prefix.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(winePrefix))
        {
            return null;
        }

        var proton = Path.Combine(winePrefix, "pfx");
        return Directory.Exists(proton)
            ? GameUserDataPaths.InWinePrefix(new WinePrefix(proton), GameUserDataPaths.PROTON_USER)
            : GameUserDataPaths.InWinePrefix(new WinePrefix(winePrefix), Environment.UserName);
    }

    private static string? MapNative(string path) => Directory.Exists(path) ? path : null;
}
