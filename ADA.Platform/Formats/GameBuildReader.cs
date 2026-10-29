using ADA.Core.Games;
using ADA.Platform.Paths;

namespace ADA.Platform.Formats;

/// <summary>
/// Reads the game's changelist from the Unreal build file shipped next to its executable, e.g.
/// <c>Engine/Binaries/Win64/FactoryGameEGS-Win64-Shipping.version</c>. This is the same for every
/// store, unlike launcher build numbers.
/// </summary>
internal static class GameBuildReader
{
    // Win64 for clients and Windows servers; Linux for the native Linux server (not yet observed).
    private static readonly string[] BINARY_FOLDERS = ["Win64", "Linux"];

    /// <summary>
    /// The build from the first readable <c>*-Shipping.version</c> file under
    /// <c>Engine/Binaries/Win64</c> or <c>Linux</c> (folder names matched ignoring case). Null when there is none.
    /// </summary>
    public static GameBuild? Read(string gamePath)
    {
        foreach (var folder in BINARY_FOLDERS)
        {
            var binaries = CaseInsensitivePath.Resolve(gamePath, ["Engine", "Binaries", folder]);
            if (binaries is null)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(binaries, "*-Shipping.version").Order(StringComparer.Ordinal))
            {
                if (TryParse(file) is { } build)
                {
                    return build;
                }
            }
        }
        return null;
    }

    private static GameBuild? TryParse(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            return GameBuild.FromVersionFile(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
