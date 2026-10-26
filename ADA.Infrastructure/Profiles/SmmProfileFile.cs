using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADA.Core.Catalog;
using ADA.Infrastructure.IO;

namespace ADA.Infrastructure.Profiles;

/// <summary>Reads and writes <c>.smmprofile</c> files (see <c>smmprofile.md</c>).</summary>
public static class SmmProfileFile
{
    public const string Extension = ".smmprofile";
    public const int LockfileVersion = 1;
    public const long MaxFileBytes = 16 * 1024 * 1024;

    // The default encoder escapes characters such as '>' for safe embedding in HTML ("\u003E=0.0.0").
    // Profile files are only ever files, so write them as other tools do (">=0.0.0").
    private static readonly SmmProfileJsonContext Writer = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <exception cref="InvalidProfileException">The file breaks any rule in <c>smmprofile.md</c>.</exception>
    public static SmmProfileDocument Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            throw new InvalidProfileException($"{info.Name} is {info.Length:N0} bytes; profiles are at most {MaxFileBytes:N0}.");
        using var stream = info.OpenRead();
        return Parse(stream);
    }

    /// <exception cref="InvalidProfileException">The JSON breaks any rule in <c>smmprofile.md</c>.</exception>
    public static SmmProfileDocument Parse(Stream json)
    {
        SmmProfileDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(json, SmmProfileJsonContext.Default.SmmProfileDocument);
        }
        catch (JsonException e)
        {
            throw new InvalidProfileException($"Not a valid .smmprofile: {e.Message}");
        }

        Validate(doc);
        return doc!;
    }

    /// <summary>Writes atomically; mods are sorted by reference, as in files other tools export.</summary>
    public static void Write(string path, SmmProfileDocument doc) => AtomicFile.Write(path, Serializer(doc));

    /// <summary>Validates and sorts the document, then returns what writes it.</summary>
    internal static Action<Stream> Serializer(SmmProfileDocument doc)
    {
        Validate(doc);
        SortOrdinally(doc);
        return stream =>
        {
            JsonSerializer.Serialize(stream, doc, Writer.SmmProfileDocument);
            stream.WriteByte((byte)'\n'); // other tools end the file with a newline
        };
    }

    /// <summary>
    /// Other tools sort mod keys by character code ("AutoLink" before "AutomatedInhalers"). A
    /// SortedDictionary built by the JSON reader uses .NET's culture-aware default comparer, which
    /// orders them differently, so re-sort everything ordinally before writing.
    /// </summary>
    private static void SortOrdinally(SmmProfileDocument doc)
    {
        if (doc.Profile?.Mods is { } mods)
            doc.Profile.Mods = new SortedDictionary<string, SmmProfileMod>(mods, StringComparer.Ordinal);
        if (doc.Lockfile?.Mods is { } locked)
        {
            doc.Lockfile.Mods = new SortedDictionary<string, SmmLockedMod>(locked, StringComparer.Ordinal);
            foreach (var mod in doc.Lockfile.Mods.Values)
            {
                if (mod.Targets is { } targets)
                    mod.Targets = new SortedDictionary<string, SmmTarget>(targets, StringComparer.Ordinal);
            }
        }
    }

    private static void Validate(SmmProfileDocument? doc)
    {
        if (doc?.Profile is null || doc.Lockfile is null)
            throw new InvalidProfileException("Not a valid .smmprofile: \"profile\" and \"lockfile\" are required.");
        if (string.IsNullOrWhiteSpace(doc.Profile.Name))
            throw new InvalidProfileException("The profile has no name.");
        if (doc.Lockfile.Version != LockfileVersion)
            throw new InvalidProfileException($"Lockfile version {doc.Lockfile.Version} is not supported (expected {LockfileVersion}).");

        foreach (var (reference, mod) in doc.Profile.Mods ?? [])
        {
            if (!ModIdentifiers.IsValidReference(reference))
                throw new InvalidProfileException($"\"{reference}\" is not a valid mod reference.");
            if (string.IsNullOrWhiteSpace(mod?.Version))
                throw new InvalidProfileException($"{reference} has no version range.");
        }

        foreach (var (reference, mod) in doc.Lockfile.Mods ?? [])
        {
            if (!ModIdentifiers.IsValidReference(reference))
                throw new InvalidProfileException($"\"{reference}\" is not a valid mod reference.");
            if (!ModIdentifiers.IsExactVersion(mod?.Version))
                throw new InvalidProfileException($"{reference} has an invalid locked version \"{mod?.Version}\".");
        }
    }
}

// Wire shapes, field for field as observed in the sample. Unknown fields at each level are kept
// in ExtensionData so a file read and written back loses nothing.

public sealed class SmmProfileDocument
{
    [JsonPropertyName("profile")] public SmmProfileSection? Profile { get; set; }
    [JsonPropertyName("lockfile")] public SmmLockfile? Lockfile { get; set; }
    [JsonPropertyName("metadata")] public SmmMetadata? Metadata { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SmmProfileSection
{
    [JsonPropertyName("mods")] public SortedDictionary<string, SmmProfileMod>? Mods { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("required_targets")] public List<string>? RequiredTargets { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SmmProfileMod
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SmmLockfile
{
    [JsonPropertyName("mods")] public SortedDictionary<string, SmmLockedMod>? Mods { get; set; }
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SmmLockedMod
{
    [JsonPropertyName("dependencies")] public JsonElement? Dependencies { get; set; }
    [JsonPropertyName("targets")] public SortedDictionary<string, SmmTarget>? Targets { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SmmTarget
{
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    [JsonPropertyName("link")] public string? Link { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SmmMetadata
{
    [JsonPropertyName("gameVersion")] public int GameVersion { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, IndentSize = 2)]
[JsonSerializable(typeof(SmmProfileDocument))]
internal sealed partial class SmmProfileJsonContext : JsonSerializerContext;
