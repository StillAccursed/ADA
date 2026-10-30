using System.Globalization;

namespace ADA.ViewModels;

/// <summary>Formats save facts for display, the same way on the list and on a save's page.</summary>
internal static class SaveText
{
    /// <summary>"142 h 10 m", or "10 m" under an hour. Hours keep counting past a day, as the game shows them.</summary>
    public static string PlayTime(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} m" : $"{time.Minutes} m";

    /// <summary>A UTC time as short local date and time, in the user's culture.</summary>
    public static string When(DateTime utc) => utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>On, Off, or Unknown where the header version doesn't record it.</summary>
    public static string OnOff(bool? value) => value switch
    {
        true => "On",
        false => "Off",
        null => "Unknown",
    };
}
