using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Overlord.Core.Profiles;
using Overlord.Infrastructure.IO;

namespace Overlord.Infrastructure.Profiles;

public sealed class InvalidProfileException(string message) : Exception(message);

/// <summary>Reads and writes Overlord's <c>.olp</c> profile files (see <c>olp.md</c>).</summary>
public static partial class OlpFile
{
    public const string Extension = ".olp";
    public const int MaxMods = 5000;
    public const long MaxFileBytes = 1024 * 1024;
    public const int MaxNameLength = 100;

    // Write profile names as typed ("Ryan's & Co"), not HTML-escaped ("Ryan\u0027s \u0026 Co").
    private static readonly OlpJsonContext Writer = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <exception cref="InvalidProfileException">The file breaks any rule in <c>olp.md</c>.</exception>
    public static ModProfile Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            throw new InvalidProfileException($"{info.Name} is {info.Length:N0} bytes; profiles are at most {MaxFileBytes:N0}.");
        using var stream = info.OpenRead();
        return Parse(stream);
    }

    /// <exception cref="InvalidProfileException">The JSON breaks any rule in <c>olp.md</c>.</exception>
    public static ModProfile Parse(Stream json)
    {
        OlpDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(json, OlpJsonContext.Default.OlpDocument);
        }
        catch (JsonException e)
        {
            throw new InvalidProfileException($"Not a valid profile: {e.Message}");
        }

        if (doc is null)
            throw new InvalidProfileException("Not a valid profile: the file is empty.");

        var name = doc.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            throw new InvalidProfileException("The profile has no name.");
        if (name.Length > MaxNameLength)
            throw new InvalidProfileException($"The profile name is longer than {MaxNameLength} characters.");
        if (doc.Mods is null)
            throw new InvalidProfileException("The profile has no \"mods\" list.");
        if (doc.Mods.Count > MaxMods)
            throw new InvalidProfileException($"The profile lists {doc.Mods.Count:N0} mods; the limit is {MaxMods:N0}.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mods = new List<ProfileMod>(doc.Mods.Count);
        for (var i = 0; i < doc.Mods.Count; i++)
        {
            var mod = doc.Mods[i] ?? throw new InvalidProfileException($"Mod #{i + 1} is empty.");
            if (mod.Name is null || !ModName().IsMatch(mod.Name))
                throw new InvalidProfileException($"Mod #{i + 1} has an invalid name \"{mod.Name}\"; names are mod references such as WeatherExtended.");
            if (mod.Version is null || !ExactVersion().IsMatch(mod.Version))
                throw new InvalidProfileException($"{mod.Name} has an invalid version \"{mod.Version}\"; versions are exact, such as 1.1.0.");
            if (!seen.Add(mod.Name))
                throw new InvalidProfileException($"{mod.Name} is listed more than once.");
            mods.Add(new ProfileMod(mod.Name, mod.Version));
        }

        return new ModProfile(name, mods);
    }

    /// <summary>Writes atomically, mods sorted by name, so the same profile always produces the same bytes.</summary>
    public static void Write(string path, ModProfile profile)
    {
        var doc = new OlpDocument(
            profile.Name,
            profile.Mods
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(m => (OlpMod?)new OlpMod(m.Name, m.Version))
                .ToList());

        // Validate what we are about to write by the same rules we read with.
        using (var check = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(doc, Writer.OlpDocument)))
            Parse(check);

        AtomicFile.Write(path, stream =>
        {
            JsonSerializer.Serialize(stream, doc, Writer.OlpDocument);
            stream.WriteByte((byte)'\n');
        });
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,100}$")]
    private static partial Regex ModName();

    // major.minor.patch, optional -prerelease and +build (semver 2.0 shape).
    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex ExactVersion();
}

internal sealed record OlpMod(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("version")] string? Version);

internal sealed record OlpDocument(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("mods")] List<OlpMod?>? Mods);

[JsonSourceGenerationOptions(WriteIndented = true, IndentSize = 2)]
[JsonSerializable(typeof(OlpDocument))]
internal sealed partial class OlpJsonContext : JsonSerializerContext;
