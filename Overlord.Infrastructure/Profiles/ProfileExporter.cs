using Overlord.Core.Catalog;
using Overlord.Core.Profiles;

namespace Overlord.Infrastructure.Profiles;

[Flags]
public enum ProfileFormats
{
    /// <summary>Overlord's own format.</summary>
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
    private const string ApiHost = "https://api.ficsit.app";

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

        var written = new List<string>();
        if (formats.HasFlag(ProfileFormats.Olp))
        {
            var path = basePath + OlpFile.Extension;
            OlpFile.Write(path, profile);
            written.Add(path);
        }
        if (smm is not null)
        {
            var path = basePath + SmmProfileFile.Extension;
            SmmProfileFile.Write(path, smm);
            written.Add(path);
        }
        return written;
    }

    /// <summary>
    /// Every mod in an Overlord profile is listed in both sections: in <c>profile</c> with the range
    /// <c>&gt;=0.0.0</c> ("any", as other tools write it) and in <c>lockfile</c> at its exact version,
    /// which is what an importer installs.
    /// </summary>
    public async Task<SmmProfileDocument> ToSmmProfileAsync(ModProfile profile, int gameChangelist,
        TargetPlatform requiredTarget, CancellationToken ct = default)
    {
        var profileMods = new SortedDictionary<string, SmmProfileMod>(StringComparer.Ordinal);
        var locked = new SortedDictionary<string, SmmLockedMod>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var mod in profile.Mods)
        {
            var versions = await getVersions(mod.Name, ct);
            var version = versions.FirstOrDefault(v => v.Version == mod.Version);
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
                        Link = ApiHost + t.DownloadPath,
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
