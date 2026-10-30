using System.Text.Json;
using ADA.Core.Games;
using ADA.Platform.Formats;

namespace ADA.Platform.Launchers;

/// <summary>
/// Reads the Epic Games Launcher's per-game <c>*.item</c> manifests and keeps completed
/// Satisfactory installs.
/// </summary>
/// <param name="userDataPath">The user-data folder for an install of the given kind; see <see cref="GameInstall.UserDataPath"/>.</param>
internal sealed class EpicManifestScanner(
    Func<string, string?> mapPath,
    GameHost host,
    string location,
    Func<GameKind, string?> userDataPath)
{
    // Epic's catalog namespace for Satisfactory (its codename is "Crab").
    public const string CATALOG_NAMESPACE = "crab";

    /// <summary>Completed Satisfactory installs from the Epic manifests folder; empty if the folder doesn't exist.</summary>
    public IReadOnlyList<GameInstall> Scan(string manifestsHostPath)
    {
        var results = new List<GameInstall>();
        if (!Directory.Exists(manifestsHostPath))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(manifestsHostPath, "*.item"))
        {
            var manifest = TryRead(file);
            if (manifest is null
                || !string.Equals(manifest.CatalogNamespace, CATALOG_NAMESPACE, StringComparison.OrdinalIgnoreCase)
                || manifest.IsIncompleteInstall
                || string.IsNullOrWhiteSpace(manifest.InstallLocation))
            {
                continue;
            }

            var gamePath = mapPath(manifest.InstallLocation);
            if (gamePath is null || GameInstallValidator.InspectKind(gamePath) is not { } kind)
            {
                continue;
            }

            results.Add(new GameInstall(gamePath, GameLauncher.Epic, host, kind, manifest.Version, location,
                GameBuildReader.Read(gamePath), userDataPath(kind)));
        }

        return results;
    }

    private sealed record Manifest(string? CatalogNamespace, string? InstallLocation, string? Version, bool IsIncompleteInstall);

    private static Manifest? TryRead(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            return new Manifest(
                GetString(root, "CatalogNamespace"),
                GetString(root, "InstallLocation"),
                GetString(root, "AppVersionString"),
                root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
