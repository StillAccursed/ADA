using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Profiles;
using ADA.Infrastructure.Installing;

namespace ADA.Infrastructure.Profiles;

/// <summary>
/// Transfers saved profiles through ADA and SMM files. Import only adds to the library; activation
/// and installation remain the profile switcher's responsibility. File work runs off the UI thread.
/// </summary>
public sealed class ProfileTransfer(ProfileStore store, IModVersionSource versions)
{
    /// <summary>
    /// Validates one .olp or .smmprofile and saves it under an unused name. SMM imports preserve locked
    /// versions and dependencies, omit explicitly disabled mods, and never use embedded download URLs.
    /// Cancellation before saving leaves the library unchanged; saving itself completes atomically.
    /// </summary>
    /// <exception cref="InvalidProfileException">The format or profile is invalid, or an enabled SMM mod has no locked version.</exception>
    public Task<ModProfile> ImportAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        var profile = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".olp" => OlpFile.Read(path),
            ".smmprofile" => ReadSmm(path),
            _ => throw new InvalidProfileException("Choose an .olp or .smmprofile file."),
        };
        ct.ThrowIfCancellationRequested();
        return store.AddImported(profile);
    }, ct);

    /// <summary>
    /// Exports a snapshot to the format named by the destination extension, using the selected
    /// installation's build and platform for SMM. SMM catalogue validation finishes before writing;
    /// ADA exports work offline. Writes atomically without changing the saved or active profile.
    /// </summary>
    /// <exception cref="InvalidProfileException">The extension is unsupported, the SMM game build is unknown, or catalogue validation fails.</exception>
    public Task ExportAsync(ModProfile profile, GameInstall game, string path, CancellationToken ct = default)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".olp" => ProfileFormats.Olp,
            ".smmprofile" => ProfileFormats.SmmProfile,
            _ => throw new InvalidProfileException("Save the profile with an .olp or .smmprofile extension."),
        };
        if (format == ProfileFormats.SmmProfile && game.Build is null)
        {
            throw new InvalidProfileException("The game build is unknown. Export as .olp instead.");
        }
        return Task.Run(async () =>
        {
            // The exporter adds a lower-case extension; retain the exact destination the picker confirmed.
            var snapshot = profile with { Mods = profile.Mods.ToArray() };
            if (format == ProfileFormats.Olp)
            {
                OlpFile.Write(path, snapshot);
            }
            else
            {
                var document = await new ProfileExporter(versions).ToSmmProfileAsync(snapshot,
                    game.Build!.Changelist, ModInstallService.TargetFor(game), ct);
                ct.ThrowIfCancellationRequested();
                SmmProfileFile.Write(path, document);
            }
        }, ct);
    }

    /// <summary>Includes locked dependency-only entries while refusing to silently lose an enabled mod.</summary>
    private static ModProfile ReadSmm(string path)
    {
        var document = SmmProfileFile.Read(path);
        var requested = new Dictionary<string, SmmProfileMod>(document.Profile!.Mods ?? [], StringComparer.OrdinalIgnoreCase);
        var locked = new Dictionary<string, SmmLockedMod>(document.Lockfile!.Mods ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var (reference, mod) in requested)
        {
            if (mod.Enabled && !locked.ContainsKey(reference))
            {
                throw new InvalidProfileException($"{reference} is enabled but has no locked version in this .smmprofile.");
            }
        }
        return new ModProfile(document.Profile.Name!, locked
            .Where(entry => !requested.TryGetValue(entry.Key, out var mod) || mod.Enabled)
            .Select(entry => new ProfileMod(entry.Key, entry.Value.Version!)).ToList());
    }
}
