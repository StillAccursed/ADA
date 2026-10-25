using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Overlord.Core.Catalog;
using Overlord.Core.Games;
using Overlord.Core.Profiles;
using Overlord.Infrastructure.Installing;

namespace Overlord.Infrastructure.Profiles;

/// <param name="Profile">The profile the mods were added to.</param>
/// <param name="Adopted">Mods Overlord now manages in the game (none when they were left alone).</param>
/// <param name="NotInProfile">Mods whose version isn't an exact version (e.g. <c>1.0</c>), so a profile can't list them.</param>
public sealed record FoundModsImport(ModProfile Profile, IReadOnlyList<InstalledMod> Adopted, IReadOnlyList<string> NotInProfile);

/// <summary>
/// Ties a game to its active profile: keeps the profile in step with what is installed, and imports
/// mods another manager installed into the current profile or a new one. Only the game's own mod
/// folders and Overlord's files are touched; another manager's files are never edited.
/// </summary>
public sealed class GameProfiles(ProfileStore store, ModInstallService installs, ILogger<GameProfiles>? log = null)
{
    private readonly ILogger _log = log ?? NullLogger<GameProfiles>.Instance;

    public Task<string> ActiveNameAsync(GameInstall game) => Task.Run(() => store.ActiveName(game.GamePath));

    public string? ProblemWithNewName(string? name) => store.ProblemWithNewName(name);

    /// <summary>Mods another manager installed that the user hasn't chosen to leave alone.</summary>
    public async Task<FoundMods> FindUnmanagedAsync(GameInstall game, CancellationToken ct = default)
    {
        var found = await installs.FindUnmanagedAsync(game, ct);
        var leftAlone = await Task.Run(() => store.LeftAlone(game.GamePath), ct);
        return found with { Mods = found.Mods.Where(m => !leftAlone.Contains(m.Location)).ToList() };
    }

    /// <summary>Rewrites the active profile as exactly what Overlord has installed; called after every change.</summary>
    public Task RecordInstalledAsync(GameInstall game) => Task.Run(() =>
    {
        var name = store.ActiveName(game.GamePath);
        var installed = installs.InstallerFor(game).Installed.Values;
        store.Save(new ModProfile(name, ToProfileMods(installed, out _)));
    });

    /// <summary>
    /// Takes over the found mods where they are and adds them to the game's current profile. Mods
    /// another found mod requires (SML, libraries) count as dependencies, removed once nothing needs them.
    /// </summary>
    public Task<FoundModsImport> ImportToCurrentAsync(GameInstall game, IReadOnlyList<FoundMod> found, CancellationToken ct = default) =>
        AdoptIntoAsync(game, found, null, ct);

    /// <summary>
    /// Saves the found mods as a new profile. When <paramref name="switchTo"/>, the game switches to
    /// it and Overlord takes the mods over; otherwise they are left where they are, and not asked about again.
    /// </summary>
    /// <exception cref="InvalidProfileException">The name is empty, too long or already used.</exception>
    public async Task<FoundModsImport> ImportToNewAsync(GameInstall game, IReadOnlyList<FoundMod> found, string name, bool switchTo,
        CancellationToken ct = default)
    {
        name = name.Trim();
        if (await Task.Run(() => store.ProblemWithNewName(name), ct) is { } problem)
            throw new InvalidProfileException(problem);

        if (switchTo)
            return await AdoptIntoAsync(game, found, name, ct);

        var mods = found.Select(m => new ProfileMod(m.Reference, m.Version)).ToList();
        var profile = new ModProfile(name, mods.Where(m => ModIdentifiers.IsExactVersion(m.Version)).ToList());
        await Task.Run(() =>
        {
            store.Save(profile);
            store.LeaveAlone(game.GamePath, found.Select(m => m.Location));
        }, ct);
        _log.LogInformation("Saved {Count} found mods in {Game} as profile {Profile}, left in place", profile.Mods.Count, game.GamePath, name);
        return new FoundModsImport(profile, [],
            mods.Where(m => !ModIdentifiers.IsExactVersion(m.Version)).Select(m => m.Name).ToList());
    }

    /// <param name="switchTo">A new profile to make active once the mods are taken over; null for the current one.</param>
    private async Task<FoundModsImport> AdoptIntoAsync(GameInstall game, IReadOnlyList<FoundMod> found, string? switchTo,
        CancellationToken ct)
    {
        // Read before adopting: with no record yet, every installed mod counts as asked for, which
        // would otherwise include the dependencies just taken over.
        var installer = installs.InstallerFor(game);
        var requestedBefore = await Task.Run(() => installer.Requested, ct);
        var adopted = await installs.AdoptAsync(game, found, ct);
        return await Task.Run(() =>
        {

            // What a found mod's .uplugin requires is a dependency, not something the user picked.
            var required = found.Where(f => adopted.Any(a => a.Reference == f.Reference))
                .SelectMany(f => f.Dependencies)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var picked = adopted.Select(a => a.Reference).Where(r => !required.Contains(r));
            installer.SaveRequested(requestedBefore.Concat(picked));

            var name = switchTo ?? store.ActiveName(game.GamePath);
            var profile = new ModProfile(name, ToProfileMods(installer.Installed.Values, out var skipped));
            store.Save(profile);
            if (switchTo is not null)
                store.SetActive(game.GamePath, switchTo);
            _log.LogInformation("Took over {Count} found mods in {Game} into profile {Profile}", adopted.Count, game.GamePath, name);
            return new FoundModsImport(profile, adopted, skipped);
        }, ct);
    }

    private static List<ProfileMod> ToProfileMods(IEnumerable<InstalledMod> installed, out IReadOnlyList<string> skipped)
    {
        var mods = installed.Select(m => new ProfileMod(m.Reference, m.Version)).ToList();
        skipped = mods.Where(m => !ModIdentifiers.IsExactVersion(m.Version)).Select(m => m.Name).ToList();
        return mods.Where(m => ModIdentifiers.IsExactVersion(m.Version)).ToList();
    }
}
