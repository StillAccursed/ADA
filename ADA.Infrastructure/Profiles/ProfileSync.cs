using System.Collections.Concurrent;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Installing;

namespace ADA.Infrastructure.Profiles;

/// <summary>How far one install is from its active profile, counting only mods players need on both sides.</summary>
/// <param name="Profile">The install's active profile; null when it has none.</param>
/// <param name="Missing">In the profile but not installed here, or installed at another version: "Storage 1.2.0".</param>
/// <param name="Extra">Installed here but not in the profile.</param>
public sealed record SyncState(string? Profile, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra)
{
    public bool InSync => Missing.Count == 0 && Extra.Count == 0;

    public int Count => Missing.Count + Extra.Count;
}

/// <summary>
/// Compares an install with its active profile. Only mods whose differing version is needed on the
/// other side of a multiplayer session count, read from ficsit.app's version list (cached), so a
/// client-only mod never makes two sides out of sync.
/// </summary>
public sealed class ProfileSync(ProfileStore store, ModInstallService installs, IModVersionSource versions)
{
    private const int PARALLEL_LOOKUPS = 4;

    /// <summary>
    /// The install's distance from its profile. A version that can't be looked up counts as needed,
    /// so a lookup failure shows as out of sync rather than hiding a difference. Reads the install's
    /// records, which for a server is a network call.
    /// </summary>
    public async Task<SyncState> CompareAsync(GameInstall game, CancellationToken ct = default)
    {
        var (profile, installed) = await Task.Run(() =>
        {
            var name = store.ActiveName(game.GamePath);
            return (name is null ? null : store.Get(name), installs.InstallerFor(game).Installed);
        }, ct);
        if (profile is null)
        {
            return new SyncState(null, [], []);
        }

        var wanted = profile.Mods.ToDictionary(m => m.Name, m => m.Version, StringComparer.OrdinalIgnoreCase);
        var differing = wanted.Keys.Concat(installed.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => wanted.GetValueOrDefault(r) != installed.GetValueOrDefault(r)?.Version)
            .ToList();

        var needed = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(differing, new ParallelOptions { MaxDegreeOfParallelism = PARALLEL_LOOKUPS, CancellationToken = ct },
            async (reference, token) => needed[reference] = await IsNeededAsync(reference,
                [wanted.GetValueOrDefault(reference), installed.GetValueOrDefault(reference)?.Version], token));

        var counted = differing.Where(r => needed[r]).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new SyncState(profile.Name,
            counted.Where(wanted.ContainsKey).Select(r => $"{r} {wanted[r]}").ToList(),
            counted.Where(r => !wanted.ContainsKey(r)).ToList());
    }

    /// <summary>Whether any of these versions of the mod must be on both sides; unknown versions count.</summary>
    private async Task<bool> IsNeededAsync(string reference, IEnumerable<string?> versionsToCheck, CancellationToken ct)
    {
        IReadOnlyList<CatalogVersion> all;
        try
        {
            all = await versions.GetVersionsAsync(reference, ct: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return true; // ficsit.app unreachable or unreadable: show the difference rather than hide it
        }
        return versionsToCheck.OfType<string>()
            .Any(v => all.FirstOrDefault(c => c.Version == v)?.RequiredOnRemote ?? true);
    }
}
