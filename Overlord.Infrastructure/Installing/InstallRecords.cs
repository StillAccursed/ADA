using System.Text.Json;
using System.Text.Json.Serialization;
using Overlord.Infrastructure.IO;

namespace Overlord.Infrastructure.Installing;

/// <param name="Path">Relative to the mod's folder, <c>/</c> separators.</param>
/// <param name="LastWriteUtc">As written at install; renames keep it, so the fast check can compare it.</param>
public sealed record InstalledFile(string Path, long Size, string Sha256, DateTime LastWriteUtc);

/// <summary>A mod Overlord installed, and the exact files it wrote.</summary>
/// <param name="Location">Relative to <c>FactoryGame</c>: <c>Mods/X</c> or <c>Mods/GameFeatures/X</c>.</param>
public sealed record InstalledMod(
    string Reference,
    string Version,
    bool GameFeature,
    string Location,
    DateTimeOffset InstalledAt,
    IReadOnlyList<InstalledFile> Files);

/// <summary>
/// Which mods Overlord installed in one game, kept in <c>FactoryGame/.overlord/installed.json</c> on the
/// same drive as <c>Mods</c>, so the record and the folders it describes can't drift apart.
/// </summary>
internal sealed class InstalledModsStore(string path)
{
    public Dictionary<string, InstalledMod> Load()
    {
        if (!File.Exists(path))
            return new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);

        using var stream = File.OpenRead(path);
        var list = JsonSerializer.Deserialize(stream, InstallJsonContext.Default.ListInstalledMod) ?? [];
        return list.ToDictionary(m => m.Reference, StringComparer.OrdinalIgnoreCase);
    }

    public void Save(IReadOnlyDictionary<string, InstalledMod> mods) =>
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream,
            mods.Values.OrderBy(m => m.Reference, StringComparer.OrdinalIgnoreCase).ToList(),
            InstallJsonContext.Default.ListInstalledMod));
}

/// <summary>
/// Which installed mods the user asked for, kept in <c>FactoryGame/.overlord/requested.json</c>. The rest
/// are dependencies, removed once nothing needs them. Only intent: <see cref="InstalledModsStore"/>
/// stays the record of what is on disk.
/// </summary>
internal sealed class RequestedModsStore(string path)
{
    /// <returns>Null if never written (mods installed before Overlord tracked this).</returns>
    public HashSet<string>? Load()
    {
        if (!File.Exists(path))
            return null;

        using var stream = File.OpenRead(path);
        var list = JsonSerializer.Deserialize(stream, InstallJsonContext.Default.ListString) ?? [];
        return list.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void Save(IEnumerable<string> references) =>
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream,
            references.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            InstallJsonContext.Default.ListString));
}

internal enum StepState
{
    /// <summary>Nothing verified yet; anything in <c>new/</c> is incomplete.</summary>
    Planned,

    /// <summary>The new version is extracted and verified in <c>new/</c>; nothing in <c>Mods</c> has changed.</summary>
    Ready,

    /// <summary>Committed: <c>Mods</c> and <c>installed.json</c> reflect this step.</summary>
    Done,
}

/// <summary>One mod's change within a transaction.</summary>
/// <param name="OldLocation">Where the currently installed version lives (null for a fresh install).</param>
/// <param name="NewLocation">Where the new version goes (null for an uninstall).</param>
/// <param name="OldRecord">Restored if the transaction is rolled back.</param>
/// <param name="NewRecord">Written once the step commits; set when the step becomes Ready.</param>
internal sealed record JournalStep(
    string Reference,
    string? OldLocation,
    string? NewLocation,
    StepState State,
    InstalledMod? OldRecord,
    InstalledMod? NewRecord);

/// <summary>Write-ahead journal: written and flushed before each change it describes.</summary>
/// <param name="Committed">
/// The installation and records are final; only working-folder and journal cleanup may be retried.
/// Defaults to false for journals written before this marker existed.
/// </param>
internal sealed record Journal(string TransactionId, DateTimeOffset StartedAt, bool RollingBack, List<JournalStep> Steps,
    bool Committed = false);

internal sealed class JournalStore(string path)
{
    public bool Exists => File.Exists(path);

    public Journal? Load()
    {
        if (!File.Exists(path))
            return null;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, InstallJsonContext.Default.Journal);
    }

    public void Save(Journal journal) =>
        AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, journal, InstallJsonContext.Default.Journal));

    public void Delete()
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<InstalledMod>))]
[JsonSerializable(typeof(Journal))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class InstallJsonContext : JsonSerializerContext;
