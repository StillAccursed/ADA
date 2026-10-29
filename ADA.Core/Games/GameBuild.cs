using System.Text.Json;

namespace ADA.Core.Games;

/// <summary>
/// The game build an install is running, read from the game's own files rather than the launcher.
/// </summary>
/// <param name="Changelist">
/// The build's changelist (CL) number, e.g. 513885. Mods and SML declare compatible games as a
/// range over this number (<c>"GameVersion": "&gt;=513885"</c> in a <c>.uplugin</c>).
/// </param>
/// <param name="GameVersion">Human-readable version, e.g. <c>1.2.4.1</c>.</param>
public sealed record GameBuild(int Changelist, string? GameVersion)
{
    /// <summary>
    /// The build in an Unreal <c>*-Shipping.version</c> file, or null if it's unreadable or has no
    /// positive <c>Changelist</c>.
    /// </summary>
    public static GameBuild? FromVersionFile(Stream file)
    {
        try
        {
            using var doc = JsonDocument.Parse(file);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Changelist", out var cl) || !cl.TryGetInt32(out var changelist) || changelist <= 0)
            {
                return null;
            }

            var version = root.TryGetProperty("GameVersion", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            return new GameBuild(changelist, version);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
