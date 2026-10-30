namespace ADA.Core.Saves;

/// <summary>One map option other than session settings, e.g. <c>skiponboarding</c>.</summary>
/// <param name="Value">The text after <c>=</c>; empty for a flag-style option.</param>
public sealed record SaveOption(string Key, string Value);

/// <summary>A save's map options, split into plain options and mod session settings.</summary>
/// <param name="Options">Options in the order the save lists them, without account identity.</param>
/// <param name="SessionSettings">SML session settings in the order the save lists them.</param>
public sealed record SaveMapOptions(IReadOnlyList<SaveOption> Options, IReadOnlyList<SessionSetting> SessionSettings)
{
    private const string SKIP_ONBOARDING_KEY = "skiponboarding";

    public static SaveMapOptions Empty { get; } = new([], []);

    /// <summary>Whether the session was started with the tutorial skipped.</summary>
    public bool SkippedOnboarding =>
        Options.Any(o => o.Key.Equals(SKIP_ONBOARDING_KEY, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A per-session setting a mod stores in the save through SML.</summary>
/// <param name="Key">The full key as saved, e.g. <c>LongerBeams.BeamLength</c>.</param>
/// <param name="ModReference">The part before the first <c>.</c>, usually the owning mod's reference; null when the key has no prefix.</param>
/// <param name="Name">The key without the mod prefix.</param>
public sealed record SessionSetting(string Key, string? ModReference, string Name, SessionSettingValue Value);

/// <summary>A decoded session setting value; the cases are the value types mods have been seen to use.</summary>
public abstract record SessionSettingValue
{
    private SessionSettingValue()
    {
    }

    public sealed record Flag(bool Value) : SessionSettingValue;

    public sealed record Integer(int Value) : SessionSettingValue;

    public sealed record Number(float Value) : SessionSettingValue;

    /// <summary>A linear (not sRGB) colour, each channel usually 0–1.</summary>
    public sealed record Colour(float R, float G, float B, float A) : SessionSettingValue;

    /// <summary>A type ADA doesn't decode, or a malformed value.</summary>
    /// <param name="TypeTag">The Unreal variant type, or null when even that couldn't be read.</param>
    public sealed record Unknown(int? TypeTag, int ByteCount) : SessionSettingValue;
}
