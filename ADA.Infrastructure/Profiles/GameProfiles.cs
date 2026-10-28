using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Profiles;
using ADA.Infrastructure.Installing;

namespace ADA.Infrastructure.Profiles;

/// <param name="Profile">The profile the mods were added to.</param>
/// <param name="Adopted">Mods ADA now manages in the game (none when they were left alone).</param>
/// <param name="NotInProfile">Mods whose version isn't an exact version (e.g. <c>1.0</c>), so a profile can't list them.</param>
public sealed record FoundModsImport(ModProfile Profile, IReadOnlyList<InstalledMod> Adopted, IReadOnlyList<string> NotInProfile);

/// <summary>
/// Ties a game to its active profile: keeps the profile in step with what is installed, and imports
/// mods another manager installed into the current profile or a new one. Only the game's own mod
/// folders and ADA's files are touched; another manager's files are never edited.
/// </summary>
public sealed class GameProfiles(ProfileStore store, ModInstallService installs, ILogger<GameProfiles>? log = null)
{
    private readonly ILogger _log = log ?? NullLogger<GameProfiles>.Instance;

    /// <summary>Forwards saved-profile changes on the saving thread; UI consumers marshal to their context.</summary>
    public event EventHandler? Changed
    {
        add => store.Changed += value;
        remove => store.Changed -= value;
    }

    public Task<string> ActiveNameAsync(GameInstall game) => Task.Run(() => store.ActiveName(game.GamePath));

    /// <summary>Whether a check or mutation currently reserves this installation.</summary>
    public bool IsChanging(GameInstall game) => installs.IsChanging(game);

    /// <summary>Lists saved profiles without creating the implicit Default profile.</summary>
    public Task<IReadOnlyList<ModProfile>> ListAsync() => Task.Run(store.List);

    /// <summary>
    /// Applies an immutable saved profile snapshot, then activates its name. Exact-version failures
    /// leave the old installation/profile intact; activation failures retain a retryable marker.
    /// </summary>
    public async Task SwitchAsync(GameInstall game, ModProfile profile, ModResolutionService resolution,
        IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        var snapshot = profile with { Mods = profile.Mods.ToArray() };
        if (string.IsNullOrWhiteSpace(snapshot.Name) || snapshot.Name.Length > OlpFile.MaxNameLength)
        {
            throw new InvalidProfileException("The profile name is empty or too long.");
        }
        await installs.ApplyProfileAsync(game, async () =>
        {
            var plan = await resolution.PlanProfileAsync(game, snapshot, ct);
            if (plan.Succeeded)
            {
                // The implicit Default becomes a saved profile only on an explicit switch away.
                await Task.Run(() =>
                {
                    var current = store.ActiveName(game.GamePath);
                    if (store.Get(current) is null)
                    {
                        store.Save(new(current, ToProfileMods(installs.InstallerFor(game).Installed.Values, out _)));
                    }
                }, ct);
            }
            return plan;
        },
            snapshot.Name, name => store.SetActive(game.GamePath, name), progress, ct);
    }

    /// <summary>Completes committed activation before any caller can record installed mods into a profile.</summary>
    public Task ReconcileAsync(GameInstall game) => Task.Run(() =>
        installs.InstallerFor(game).CompleteProfileActivation(name => store.SetActive(game.GamePath, name)));

    public string? ProblemWithNewName(string? name) => store.ProblemWithNewName(name);

    /// <summary>Mods another manager installed that the user hasn't chosen to leave alone.</summary>
    public async Task<FoundMods> FindUnmanagedAsync(GameInstall game, CancellationToken ct = default)
    {
        var found = await installs.FindUnmanagedAsync(game, ct);
        var leftAlone = await Task.Run(() => store.LeftAlone(game.GamePath), ct);
        return found with { Mods = found.Mods.Where(m => !leftAlone.Contains(m.Location)).ToList() };
    }

    /// <summary>Rewrites the active profile as exactly what ADA has installed; called after every change.</summary>
    public async Task RecordInstalledAsync(GameInstall game)
    {
        await ReconcileAsync(game);
        await Task.Run(() =>
        {
            var installer = installs.InstallerFor(game);
            if (installer.HasInterruptedTransaction || installer.HasPendingProfileActivation)
            {
                throw new InstallException("Finish pending recovery before recording this profile.");
            }
            var name = store.ActiveName(game.GamePath);
            store.Save(new ModProfile(name, ToProfileMods(installer.Installed.Values, out _)));
        });
    }

    /// <summary>
    /// Takes over the found mods where they are and adds them to the game's current profile. Mods
    /// another found mod requires (SML, libraries) count as dependencies, removed once nothing needs them.
    /// </summary>
    public Task<FoundModsImport> ImportToCurrentAsync(GameInstall game, IReadOnlyList<FoundMod> found, CancellationToken ct = default) =>
        AdoptIntoAsync(game, found, null, ct);

    /// <summary>
    /// Saves the found mods as a new profile. When <paramref name="switchTo"/>, the game switches to
    /// it and ADA takes the mods over; otherwise they are left where they are, and not asked about again.
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
