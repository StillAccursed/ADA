using Overlord.Core.Games;
using Overlord.Platform.Formats;
using Overlord.Platform.Paths;

namespace Overlord.Platform.Launchers;

/// <summary>
/// Finds Satisfactory in every Steam library listed by a Steam install's <c>libraryfolders.vdf</c>,
/// trusting only app manifests that Steam marks as fully installed.
/// </summary>
/// <param name="mapPath">
/// Turns a library path as Steam wrote it into a host path: identity on native installs,
/// <see cref="WinePrefix.ToHostPath"/> inside a bottle.
/// </param>
public sealed class SteamLibraryScanner(Func<string, string?> mapPath, GameHost clientHost, string location)
{
    public const string ClientAppId = "526870";
    public const string ServerAppId = "1690800";

    // Steam's AppState StateFlags bit for "fully installed".
    private const int StateFullyInstalled = 4;

    public IReadOnlyList<GameInstall> Scan(string steamRootHostPath)
    {
        var results = new List<GameInstall>();
        foreach (var library in LibraryHostPaths(steamRootHostPath))
        {
            TryAdd(results, library, ClientAppId, GameKind.Client, clientHost);
            var serverHost = clientHost == GameHost.LinuxCompat ? GameHost.Linux : clientHost;
            TryAdd(results, library, ServerAppId, GameKind.DedicatedServer, serverHost);
        }
        return results;
    }

    private IEnumerable<string> LibraryHostPaths(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(steamRoot) && seen.Add(Normalize(steamRoot)))
            yield return steamRoot;

        var vdfPath = CaseInsensitivePath.Resolve(steamRoot, ["steamapps", "libraryfolders.vdf"]);
        var folders = vdfPath is null ? null : VdfNode.TryLoad(vdfPath)?["libraryfolders"];
        if (folders is null)
            yield break;

        foreach (var entry in folders.Children.Values)
        {
            // Current format: "0" { "path" "..." }. Older format: "1" "D:\\SteamLibrary".
            var raw = entry.Value ?? entry.GetString("path");
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var host = mapPath(raw);
            if (host is not null && seen.Add(Normalize(host)))
                yield return host;
        }
    }

    private void TryAdd(List<GameInstall> results, string library, string appId, GameKind kind, GameHost host)
    {
        var acfPath = CaseInsensitivePath.Resolve(library, ["steamapps", $"appmanifest_{appId}.acf"]);
        var state = acfPath is null ? null : VdfNode.TryLoad(acfPath)?["AppState"];
        if (state is null)
            return;

        if (!int.TryParse(state.GetString("StateFlags"), out var flags) || (flags & StateFullyInstalled) == 0)
            return;

        var installDir = state.GetString("installdir");
        if (string.IsNullOrWhiteSpace(installDir))
            return;

        var gamePath = CaseInsensitivePath.Resolve(library, ["steamapps", "common", installDir]);
        if (gamePath is null || !GameInstallValidator.LooksLikeGame(gamePath))
            return;

        results.Add(new GameInstall(gamePath, GameLauncher.Steam, host, kind, state.GetString("buildid"), location,
            GameBuildReader.Read(gamePath)));
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
