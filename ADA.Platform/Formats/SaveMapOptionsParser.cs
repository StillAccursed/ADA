using System.Buffers.Binary;
using ADA.Core.Saves;

namespace ADA.Platform.Formats;

/// <summary>
/// Splits a save's map options (<c>?key?key=value…</c>) into plain options and SML session settings,
/// dropping the options that identify the player's platform account. Never throws: anything
/// unreadable becomes an <see cref="SessionSettingValue.Unknown"/> value or is skipped.
/// </summary>
internal static class SaveMapOptionsParser
{
    private const string SESSION_SETTINGS_KEY = "SessionSettings";

    // Unreal EVariantTypes values, as found in real saves' session settings.
    private const int VARIANT_BOOL = 2;
    private const int VARIANT_FLOAT = 10;
    private const int VARIANT_INT32 = 14;
    private const int VARIANT_LINEAR_COLOR = 17;

    // An FVariant is serialized as its type tag, then its value as a byte array (count, then bytes).
    private const int VARIANT_HEADER_BYTES = 8;

    // Account ids and names: never shown, logged or kept.
    private static readonly string[] PRIVATE_KEYS = ["ClientIdentity", "PlayerInfoSnapshot", "FallbackAccountNames"];

    public static SaveMapOptions Parse(string mapOptions)
    {
        var options = new List<SaveOption>();
        var settings = new List<SessionSetting>();
        foreach (var part in mapOptions.Split('?', StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = SplitPair(part);
            if (key.Equals(SESSION_SETTINGS_KEY, StringComparison.OrdinalIgnoreCase))
            {
                settings.AddRange(ParseSessionSettings(value));
            }
            else if (!PRIVATE_KEYS.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                options.Add(new SaveOption(key, value));
            }
        }
        return new SaveMapOptions(options, settings);
    }

    private static IEnumerable<SessionSetting> ParseSessionSettings(string list)
    {
        foreach (var entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (key, value) = SplitPair(entry);
            if (key.Length == 0)
            {
                continue;
            }

            var dot = key.IndexOf('.');
            yield return dot > 0
                ? new SessionSetting(key, key[..dot], key[(dot + 1)..], DecodeVariant(value))
                : new SessionSetting(key, null, key, DecodeVariant(value));
        }
    }

    /// <summary>Splits at the first <c>=</c>, so base64 padding stays in the value.</summary>
    private static (string Key, string Value) SplitPair(string text)
    {
        var equals = text.IndexOf('=');
        return equals < 0 ? (text, "") : (text[..equals], text[(equals + 1)..]);
    }

    private static SessionSettingValue DecodeVariant(string base64)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return new SessionSettingValue.Unknown(null, 0);
        }

        if (bytes.Length < VARIANT_HEADER_BYTES)
        {
            return new SessionSettingValue.Unknown(null, bytes.Length);
        }

        var type = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        var value = bytes.AsSpan(VARIANT_HEADER_BYTES);
        if (count != value.Length)
        {
            return new SessionSettingValue.Unknown(type, value.Length);
        }

        return (type, value.Length) switch
        {
            (VARIANT_BOOL, 4) => new SessionSettingValue.Flag(BinaryPrimitives.ReadInt32LittleEndian(value) != 0),
            (VARIANT_INT32, 4) => new SessionSettingValue.Integer(BinaryPrimitives.ReadInt32LittleEndian(value)),
            (VARIANT_FLOAT, 4) => new SessionSettingValue.Number(BinaryPrimitives.ReadSingleLittleEndian(value)),
            (VARIANT_LINEAR_COLOR, 16) => new SessionSettingValue.Colour(
                BinaryPrimitives.ReadSingleLittleEndian(value),
                BinaryPrimitives.ReadSingleLittleEndian(value[4..]),
                BinaryPrimitives.ReadSingleLittleEndian(value[8..]),
                BinaryPrimitives.ReadSingleLittleEndian(value[12..])),
            _ => new SessionSettingValue.Unknown(type, value.Length),
        };
    }
}
