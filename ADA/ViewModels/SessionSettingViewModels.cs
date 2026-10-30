using System.Globalization;
using ADA.Core.Saves;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ADA.ViewModels;

/// <summary>One mod's session settings on the save page's Options tab.</summary>
public sealed record SessionSettingGroupViewModel(string Title, IReadOnlyList<SessionSettingRowViewModel> Settings)
{
    public string Header => $"{Title} ({Settings.Count})";
}

/// <summary>One decoded session setting.</summary>
public sealed class SessionSettingRowViewModel(SessionSetting setting)
{
    private const string UNREADABLE = "(not readable)";

    public string Name => setting.Name;

    public string Key => setting.Key;

    public string ValueText { get; } = Format(setting.Value);

    /// <summary>A swatch of a colour setting, in sRGB; null for other settings.</summary>
    public IBrush? Swatch { get; } = setting.Value is SessionSettingValue.Colour colour
        ? new ImmutableSolidColorBrush(Color.FromArgb(ToByte(colour.A), ToSrgbByte(colour.R), ToSrgbByte(colour.G), ToSrgbByte(colour.B)))
        : null;

    public bool HasSwatch => Swatch is not null;

    internal static string Format(SessionSettingValue value) => value switch
    {
        SessionSettingValue.Flag flag => flag.Value ? "On" : "Off",
        SessionSettingValue.Integer integer => integer.Value.ToString(CultureInfo.CurrentCulture),
        SessionSettingValue.Number number => number.Value.ToString(CultureInfo.CurrentCulture),
        SessionSettingValue.Colour colour => $"#{ToSrgbByte(colour.R):X2}{ToSrgbByte(colour.G):X2}{ToSrgbByte(colour.B):X2}",
        _ => UNREADABLE,
    };

    /// <summary>A linear channel (0–1) as an sRGB byte, as screens show it; out-of-range values are clamped.</summary>
    internal static byte ToSrgbByte(float linear)
    {
        var c = Math.Clamp(linear, 0f, 1f);
        var srgb = c <= 0.0031308f ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (byte)Math.Round(srgb * 255, MidpointRounding.AwayFromZero);
    }

    private static byte ToByte(float alpha) => (byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255, MidpointRounding.AwayFromZero);
}
