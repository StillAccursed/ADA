using System.Text;
using System.Text.Json;
using ADA.Core.Saves;

namespace ADA.Platform.Formats;

/// <summary>
/// Reads the uncompressed header at the start of a Satisfactory <c>.sav</c>, stopping before the
/// world data that follows. Anything that isn't a readable save (a save from before Update 5,
/// <c>ServerManager_V2.sav</c>, a truncated or corrupt file) gives null.
/// </summary>
public static class SaveHeaderReader
{
    private const int MIN_HEADER_VERSION = 8;

    private const int SAVE_NAME_HEADER_VERSION = 14;

    // The only version whose fields after the mod list have been checked against real saves.
    private const int VERIFIED_TAIL_HEADER_VERSION = 14;

    // Well above any version the game is likely to reach, so newer saves are still tried, yet low
    // enough that a file starting with text (e.g. "MSGF") is rejected at once.
    private const int MAX_PLAUSIBLE_HEADER_VERSION = 64;

    // Real header strings reach tens of KB (map options, mod lists); this stops a corrupt length
    // from allocating a huge buffer.
    private const int MAX_STRING_BYTES = 1024 * 1024;

    // The save data hash: a flag, then an MD5.
    private const int SAVE_HASH_BYTES = 4 + 16;

    private const int READ_BUFFER_BYTES = 64 * 1024;

    /// <summary>
    /// The header of the save at <paramref name="path"/>, or null if it can't be read or isn't a save.
    /// Opens the file sharing read, write and delete, so it never blocks the game mid-autosave.
    /// </summary>
    public static SaveHeader? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, READ_BUFFER_BYTES);
            return Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads a header from the stream's current position; leaves the stream open.</summary>
    internal static SaveHeader? Read(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: true);
            var headerVersion = reader.ReadInt32();
            if (headerVersion is < MIN_HEADER_VERSION or > MAX_PLAUSIBLE_HEADER_VERSION)
            {
                return null;
            }

            var saveVersion = reader.ReadInt32();
            var buildVersion = reader.ReadInt32();
            var saveName = headerVersion >= SAVE_NAME_HEADER_VERSION ? ReadString(reader) : null;
            var mapName = ReadString(reader);
            var mapOptions = ReadString(reader);
            var sessionName = ReadString(reader);
            var playSeconds = reader.ReadInt32();
            var savedAtTicks = reader.ReadInt64();
            var visibility = (SessionVisibility)reader.ReadByte();
            reader.ReadInt32(); // Editor object version.
            var modMetadata = ReadString(reader);
            var isModded = reader.ReadInt32() != 0;

            if (sessionName.Length == 0 || playSeconds < 0 || savedAtTicks <= 0 || savedAtTicks > DateTime.MaxValue.Ticks)
            {
                return null;
            }

            var tail = headerVersion == VERIFIED_TAIL_HEADER_VERSION ? ReadTail(reader) : default;
            return new SaveHeader(headerVersion, saveVersion, buildVersion, saveName, mapName, sessionName,
                TimeSpan.FromSeconds(playSeconds), new DateTime(savedAtTicks, DateTimeKind.Utc), visibility, isModded,
                ParseMods(modMetadata), SaveMapOptionsParser.Parse(mapOptions),
                tail.SaveIdentifier, tail.IsPartitionedWorld, tail.AdvancedGameSettings);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>The fields after "is modded", in header version 14's layout.</summary>
    private static (string? SaveIdentifier, bool? IsPartitionedWorld, bool? AdvancedGameSettings) ReadTail(BinaryReader reader)
    {
        var saveIdentifier = ReadString(reader);
        var isPartitioned = reader.ReadInt32() != 0;
        SkipBytes(reader, SAVE_HASH_BYTES);
        var advancedGameSettings = reader.ReadInt32() != 0;
        return (saveIdentifier, isPartitioned, advancedGameSettings);
    }

    /// <summary>Reads an Unreal <c>FString</c>, without its null terminator.</summary>
    /// <exception cref="InvalidDataException">The length is implausible or the terminator is missing.</exception>
    /// <exception cref="EndOfStreamException">The file ends inside the string.</exception>
    private static string ReadString(BinaryReader reader)
    {
        long length = reader.ReadInt32();
        if (length == 0)
        {
            return "";
        }

        var isUtf16 = length < 0;
        var byteCount = isUtf16 ? -length * 2 : length;
        if (byteCount > MAX_STRING_BYTES)
        {
            throw new InvalidDataException($"Save header string of {byteCount} bytes is implausibly long.");
        }

        var bytes = reader.ReadBytes((int)byteCount);
        if (bytes.Length != byteCount)
        {
            throw new EndOfStreamException();
        }

        var text = isUtf16 ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        if (!text.EndsWith('\0'))
        {
            throw new InvalidDataException("Save header string isn't null-terminated.");
        }
        return text[..^1];
    }

    private static void SkipBytes(BinaryReader reader, int count)
    {
        if (reader.ReadBytes(count).Length != count)
        {
            throw new EndOfStreamException();
        }
    }

    /// <summary>
    /// The mods in the header's metadata JSON (<c>{"Version":1,"Mods":[{"Reference","Name","Version"}]}</c>).
    /// Unreadable JSON gives an empty list rather than rejecting a save that is otherwise fine.
    /// </summary>
    private static IReadOnlyList<SaveMod> ParseMods(string json)
    {
        if (json.Length == 0)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("Mods", out var mods)
                || mods.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<SaveMod>();
            foreach (var mod in mods.EnumerateArray())
            {
                if (Text(mod, "Reference") is { Length: > 0 } reference)
                {
                    result.Add(new SaveMod(reference, Text(mod, "Name") ?? reference, Text(mod, "Version")));
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
