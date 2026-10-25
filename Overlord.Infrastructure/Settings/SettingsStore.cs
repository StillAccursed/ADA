using System.Text.Json;
using System.Text.Json.Serialization;
using Overlord.Core.Settings;

namespace Overlord.Infrastructure.Settings;

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore(string settingsPath)
{
    public string SettingsPath { get; } = settingsPath;

    /// <summary>
    /// The saved settings, or defaults if the file is missing or unreadable. A damaged file never
    /// stops the app from starting; it is replaced on the next save.
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return AppSettings.Default;

            using var stream = File.OpenRead(SettingsPath);
            return (JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? AppSettings.Default).Normalized();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppSettings.Default;
        }
    }

    /// <summary>
    /// Writes to a temporary file, flushes it to disk, then renames it over the old file, so a
    /// crash mid-save leaves the previous settings intact rather than a half-written file.
    /// </summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!);
        var temp = SettingsPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings.Normalized(), SettingsJsonContext.Default.AppSettings);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, SettingsPath, overwrite: true);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
