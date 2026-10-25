using System.Text.Json;

namespace Overlord.Infrastructure.Installing;

/// <param name="Name">The plugin (mod reference) this one depends on, e.g. <c>SML</c>.</param>
/// <param name="SemVersion">Semver range, e.g. <c>^3.12.0</c>; null if the entry gives none.</param>
public sealed record UPluginDependency(string Name, string? SemVersion, bool Optional);

/// <summary>The fields of a <c>.uplugin</c> file that Overlord uses (see <c>smod.md</c>).</summary>
/// <param name="Reference">The file name without extension; also the mod's folder name.</param>
/// <param name="GameFeature">True: installs under <c>Mods/GameFeatures/</c>; false or absent: <c>Mods/</c>.</param>
public sealed record UPluginDescriptor(
    string Reference,
    string Version,
    string? FriendlyName,
    string? GameVersion,
    bool GameFeature,
    IReadOnlyList<UPluginDependency> Dependencies)
{
    // Authors sometimes edit .uplugin files by hand.
    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <exception cref="InvalidModArchiveException">Not valid JSON, or no version.</exception>
    public static UPluginDescriptor Parse(string reference, Stream json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, Lenient);
            var root = doc.RootElement;
            var version = String(root, "SemVersion") ?? String(root, "VersionName")
                          ?? throw new InvalidModArchiveException($"{reference}.uplugin has no SemVersion.");

            var dependencies = new List<UPluginDependency>();
            if (root.TryGetProperty("Plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
            {
                foreach (var plugin in plugins.EnumerateArray())
                {
                    if (String(plugin, "Name") is { } name)
                        dependencies.Add(new UPluginDependency(name, String(plugin, "SemVersion"), Bool(plugin, "Optional")));
                }
            }

            return new UPluginDescriptor(reference, version, String(root, "FriendlyName"), String(root, "GameVersion"),
                Bool(root, "GameFeature"), dependencies);
        }
        catch (JsonException e)
        {
            throw new InvalidModArchiveException($"{reference}.uplugin is not valid JSON: {e.Message}");
        }
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
