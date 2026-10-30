using ADA.Core.Saves;
using ADA.Infrastructure.Installing;

namespace ADA.ViewModels;

/// <summary>How a mod recorded in a save compares with the selected install; the order is the Mods tab's order.</summary>
public enum SaveModStatus
{
    Missing,
    DifferentVersion,
    Installed,

    /// <summary>The install's mods couldn't be read.</summary>
    Unknown,
}

/// <summary>One mod recorded in a save, on the save page's Mods tab.</summary>
public sealed class SaveModRowViewModel(SaveMod mod, SaveModStatus status, string? installedVersion)
{
    public string Name => mod.Name;

    public string Reference => mod.Reference;

    public SaveModStatus Status => status;

    public string SavedVersionText => mod.Version is null ? "Version not recorded" : $"Saved with {mod.Version}";

    public string StatusText => status switch
    {
        SaveModStatus.Installed => "Installed",
        SaveModStatus.DifferentVersion => $"{installedVersion} installed",
        SaveModStatus.Missing => "Not installed",
        _ => "",
    };

    public bool IsInstalled => status == SaveModStatus.Installed;

    public bool IsDifferentVersion => status == SaveModStatus.DifferentVersion;

    public bool IsMissing => status == SaveModStatus.Missing;

    /// <param name="installed">The install's mods by reference, ignoring case; null when they couldn't be read.</param>
    internal static SaveModRowViewModel Compare(SaveMod mod, IReadOnlyDictionary<string, InstalledMod>? installed)
    {
        if (installed is null)
        {
            return new SaveModRowViewModel(mod, SaveModStatus.Unknown, null);
        }
        if (!installed.TryGetValue(mod.Reference, out var match))
        {
            return new SaveModRowViewModel(mod, SaveModStatus.Missing, null);
        }

        var same = mod.Version is null || string.Equals(mod.Version, match.Version, StringComparison.OrdinalIgnoreCase);
        return new SaveModRowViewModel(mod, same ? SaveModStatus.Installed : SaveModStatus.DifferentVersion, match.Version);
    }
}
