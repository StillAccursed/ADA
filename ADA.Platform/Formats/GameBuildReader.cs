using System.Text.Json;
using ADA.Core.Games;
using ADA.Platform.Paths;

namespace ADA.Platform.Formats;

/// <summary>
/// Reads the game's changelist from the Unreal build file shipped next to its executable, e.g.
/// <c>Engine/Binaries/Win64/FactoryGameEGS-Win64-Shipping.version</c>. This is the same for every
/// store, unlike launcher build numbers.
/// </summary>
public static class GameBuildReader
{
    // Win64 for clients and Windows servers; Linux for the native Linux server (not yet observed).
    private static readonly string[] BinaryFolders = ["Win64", "Linux"];

    public static GameBuild? Read(string gamePath)
    {
        foreach (var folder in BinaryFolders)
        {
            var binaries = CaseInsensitivePath.Resolve(gamePath, ["Engine", "Binaries", folder]);
            if (binaries is null)
                continue;

            foreach (var file in Directory.EnumerateFiles(binaries, "*-Shipping.version").Order(StringComparer.Ordinal))
            {
                if (TryParse(file) is { } build)
                    return build;
            }
        }
        return null;
    }

    private static GameBuild? TryParse(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (!root.TryGetProperty("Changelist", out var cl) || !cl.TryGetInt32(out var changelist) || changelist <= 0)
                return null;

            var version = root.TryGetProperty("GameVersion", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            return new GameBuild(changelist, version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
