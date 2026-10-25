using System.Text.Json;
using System.Text.Json.Serialization;
using Overlord.Core.Settings;
using Overlord.Infrastructure.IO;

namespace Overlord.Infrastructure.Settings;

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore(string settingsPath)
{
    private readonly Lock _gate = new();
    private AppSettings? _current;

    public string SettingsPath { get; } = settingsPath;

    /// <summary>The settings in effect: read from disk once, then kept in memory and updated by <see cref="Save"/>.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
                return _current ??= Load();
        }
    }

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
    /// Changes some settings and saves, starting from the settings in effect, so pages that each
    /// own a few settings never write back another page's old values.
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        lock (_gate)
            SaveLocked(change(_current ??= Load()).Normalized());
    }

    /// <summary>
    /// Writes to a temporary file, flushes it to disk, then renames it over the old file, so a
    /// crash mid-save leaves the previous settings intact rather than a half-written file.
    /// </summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
            SaveLocked(settings.Normalized());
    }

    private void SaveLocked(AppSettings settings)
    {
        _current = settings;
        AtomicFile.Write(SettingsPath, stream => JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings));
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
