using System.Globalization;

namespace ADA.ViewModels;

/// <summary>Formats byte counts for display in the user's culture, e.g. "512 bytes", "4.2 MB", "37 GB".</summary>
internal static class ByteSize
{
    /// <summary>Uses binary (1024) steps; one decimal place below 10 of a unit, none above.</summary>
    internal static string Format(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} bytes"
            : $"{size.ToString(size < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
