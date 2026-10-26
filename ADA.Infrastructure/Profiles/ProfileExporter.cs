using ADA.Core.Catalog;
using ADA.Core.Profiles;
using ADA.Infrastructure.Ficsit;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Threading;

namespace ADA.Infrastructure.Profiles;

[Flags]
public enum ProfileFormats
{
    /// <summary>ADA's own format.</summary>
    Olp = 1,

    /// <summary>The format Satisfactory Mod Manager imports.</summary>
    SmmProfile = 2,

    Both = Olp | SmmProfile,
}

/// <summary>
/// Writes a profile as <c>.olp</c>, <c>.smmprofile</c>, or both. A <c>.smmprofile</c> has to be
/// complete on its own (Satisfactory Mod Manager imports only that file), so its lockfile is filled
/// with each mod's download link and hash from the ficsit.app catalogue.
/// </summary>
public sealed class ProfileExporter(Func<string, CancellationToken, Task<IReadOnlyList<CatalogVersion>>> getVersions)
{
    private const int ParallelLookups = 4;

    /// <param name="basePath">Path without extension; each format adds its own.</param>
    /// <param name="gameChangelist">The game build the profile is for, written to <c>metadata.gameVersion</c>.</param>
    /// <param name="requiredTarget">The platform every mod must have a build for (<c>Windows</c> for clients).</param>
    /// <returns>The files written. If anything fails, nothing is written.</returns>
    /// <exception cref="InvalidProfileException">A mod's version is not in the catalogue, or lacks a build for the target.</exception>
    public async Task<IReadOnlyList<string>> ExportAsync(ModProfile profile, string basePath, ProfileFormats formats,
        int gameChangelist, TargetPlatform requiredTarget, CancellationToken ct = default)
    {
        // Build everything that can fail before writing anything.
        var smm = formats.HasFlag(ProfileFormats.SmmProfile)
            ? await ToSmmProfileAsync(profile, gameChangelist, requiredTarget, ct)
            : null;

        var files = new List<(string Path, Action<Stream> Write)>();
        if (formats.HasFlag(ProfileFormats.Olp))
            files.Add((basePath + OlpFile.Extension, OlpFile.Serializer(profile)));
        if (smm is not null)
            files.Add((basePath + SmmProfileFile.Extension, SmmProfileFile.Serializer(smm)));

        await Task.Run(() => AtomicFile.WriteAll(files), ct);
        return files.Select(f => f.Path).ToList();
    }

    /// <summary>
    /// Every mod in an ADA profile is listed in both sections: in <c>profile</c> with the range
    /// <c>&gt;=0.0.0</c> ("any", as other tools write it) and in <c>lockfile</c> at its exact version,
    /// which is what an importer installs.
    /// </summary>
    public async Task<SmmProfileDocument> ToSmmProfileAsync(ModProfile profile, int gameChangelist,
        TargetPlatform requiredTarget, CancellationToken ct = default)
    {
        var profileMods = new SortedDictionary<string, SmmProfileMod>(StringComparer.Ordinal);
        var locked = new SortedDictionary<string, SmmLockedMod>(StringComparer.Ordinal);
        var problems = new List<string>();

        var found = await Concurrently.MapAsync(profile.Mods, ParallelLookups, async (mod, token) =>
            (await getVersions(mod.Name, token)).FirstOrDefault(v => v.Version == mod.Version), ct);

        for (var i = 0; i < profile.Mods.Count; i++)
        {
            var mod = profile.Mods[i];
            var version = found[i];
            if (version is null)
            {
                problems.Add($"{mod.Name} {mod.Version} is not listed on ficsit.app.");
                continue;
            }
            if (version.TargetFor(requiredTarget) is null)
            {
                problems.Add($"{mod.Name} {mod.Version} has no {requiredTarget} build.");
                continue;
            }

            profileMods[mod.Name] = new SmmProfileMod { Version = ">=0.0.0", Enabled = true };
            locked[mod.Name] = new SmmLockedMod
            {
                Version = mod.Version,
                Dependencies = null,
                Targets = new SortedDictionary<string, SmmTarget>(
                    version.Targets.ToDictionary(t => t.Platform.ToString(), t => new SmmTarget
                    {
                        Link = FicsitApi.AbsoluteLink(t.DownloadPath),
                        Hash = t.Hash,
                    }),
                    StringComparer.Ordinal),
            };
        }

        if (problems.Count > 0)
            throw new InvalidProfileException("Can't export as .smmprofile: " + string.Join(" ", problems));

        return new SmmProfileDocument
        {
            Profile = new SmmProfileSection { Name = profile.Name, Mods = profileMods, RequiredTargets = [requiredTarget.ToString()] },
            Lockfile = new SmmLockfile { Version = SmmProfileFile.LockfileVersion, Mods = locked },
            Metadata = new SmmMetadata { GameVersion = gameChangelist },
        };
    }
}
