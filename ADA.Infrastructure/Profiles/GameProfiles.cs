using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Profiles;
using ADA.Infrastructure.Installing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Profiles;

/// <param name="Profile">The profile the mods were added to.</param>
/// <param name="Adopted">Mods ADA now manages in the game (none when they were left alone).</param>
/// <param name="NotInProfile">Mods whose version isn't an exact version (e.g. <c>1.0</c>), so a profile can't list them.</param>
public sealed record FoundModsImport(ModProfile Profile, IReadOnlyList<InstalledMod> Adopted, IReadOnlyList<string> NotInProfile);

/// <summary>What switching to a profile would change, with its saved versions and with newer ones.</summary>
/// <param name="Saved">Every profile mod at its saved version; may have failed, e.g. a version that's gone.</param>
/// <param name="Newest">
/// The newest versions that work together, never older than saved; null when they're the same as
/// <paramref name="Saved"/> or can't be installed either.
/// </param>
public sealed record ProfileSwitchPreview(ModChangePlan Saved, ModChangePlan? Newest)
{
    /// <summary>The user can choose newer versions than the profile saved.</summary>
    public bool OffersNewer => Newest is not null;
}

/// <summary>
/// Ties a game to its active profile: keeps the profile in step with what is installed, and imports
/// mods another manager installed into the current profile or a new one. Only the game's own mod
/// folders and ADA's files are touched; another manager's files are never edited.
/// </summary>
public sealed class GameProfiles(ProfileStore store, ModInstallService installs, ILogger<GameProfiles>? log = null)
{
    private const string UNSAVED_MODS_PROBLEM = "Create a profile for the installed mods before switching, so they can be restored later.";

    private readonly ILogger _log = log ?? NullLogger<GameProfiles>.Instance;

    /// <summary>Forwards saved-profile changes on the saving thread; UI consumers marshal to their context.</summary>
    public event EventHandler? Changed
    {
        add => store.Changed += value;
        remove => store.Changed -= value;
    }

    /// <summary>The game's active profile, or null when none has been created or chosen for it yet.</summary>
    public Task<string?> ActiveNameAsync(GameInstall game) => Task.Run(() => store.ActiveName(game.GamePath));

    /// <summary>Whether a check or mutation currently reserves this installation.</summary>
    public bool IsChanging(GameInstall game) => installs.IsChanging(game);

    /// <summary>Lists saved profiles, sorted by name. Empty until the user creates the first one.</summary>
    public Task<IReadOnlyList<ModProfile>> ListAsync() => Task.Run(store.List);

    /// <summary>
    /// Deletes a saved profile and clears all of its active selections, without changing installed
    /// mods. Reserves the known installations and refuses pending recovery before deleting, so a
    /// concurrent operation or committed switch cannot recreate the deleted profile.
    /// Cancellation is observed before deletion; the store then finishes its save-and-remove step.
    /// </summary>
    /// <exception cref="InstallationBusyException">A known installation is already reserved.</exception>
    /// <exception cref="InstallException">Recovery or profile activation is pending.</exception>
    /// <exception cref="InvalidProfileException">The profile no longer exists.</exception>
    public Task DeleteAsync(string name, IReadOnlyList<GameInstall> games, CancellationToken ct = default) => Task.Run(() =>
    {
        var leases = new List<IDisposable>();
        try
        {
            var known = games.DistinctBy(game => game.GamePath).ToList();
            foreach (var game in known)
            {
                ct.ThrowIfCancellationRequested();
                leases.Add(installs.BeginOperation(game));
            }
            foreach (var game in known)
            {
                installs.InstallerFor(game).ThrowIfUnsettled("deleting a profile");
            }
            ct.ThrowIfCancellationRequested();
            store.Delete(name);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }, ct);

    /// <summary>
    /// Saves the mods ADA manages in <paramref name="game"/> as a new profile and makes it the game's
    /// active profile. Used for a game's first profile, and to start a new playthrough's profile
    /// from what is installed. Holds the installation for the whole read-and-save.
    /// </summary>
    /// <exception cref="InvalidProfileException">The name is empty, too long or already used.</exception>
    /// <exception cref="InstallationBusyException">Another operation holds this installation.</exception>
    /// <exception cref="InstallException">Recovery or a profile switch is still pending.</exception>
    public async Task<ModProfile> CreateAsync(GameInstall game, string name, CancellationToken ct = default)
    {
        name = name.Trim();
        using var operation = installs.BeginOperation(game);
        await ReconcileAsync(game);
        return await Task.Run(() =>
        {
            if (store.ProblemWithNewName(name) is { } problem)
            {
                throw new InvalidProfileException(problem);
            }
            var installer = installs.InstallerFor(game);
            installer.ThrowIfUnsettled("creating a profile");
            var profile = new ModProfile(name, ToProfileMods(installer.Installed.Values, out _));
            store.Save(profile);
            store.SetActive(game.GamePath, name);
            _log.LogInformation("Created profile {Profile} with {Count} mods for {Game}", name, profile.Mods.Count, game.GamePath);
            return profile;
        }, ct);
    }

    /// <summary>
    /// Works out what switching <paramref name="game"/> to <paramref name="profile"/> would change,
    /// with the profile's saved versions and with newer ones, by the same rules as
    /// <see cref="SwitchAsync"/>. Reads only: nothing is reserved, downloaded or written.
    /// </summary>
    /// <exception cref="InvalidProfileException">The profile's name is empty or too long.</exception>
    public async Task<ProfileSwitchPreview> PreviewSwitchAsync(GameInstall game, ModProfile profile,
        ModResolutionService resolution, CancellationToken ct = default)
    {
        var snapshot = Snapshot(profile);
        if (await HasUnsavedModsAsync(game, ct))
        {
            return new ProfileSwitchPreview(ModChangePlan.Failed([UNSAVED_MODS_PROBLEM]), null);
        }
        var saved = await resolution.PlanProfileAsync(game, snapshot, newest: false, ct);
        var newest = await resolution.PlanProfileAsync(game, snapshot, newest: true, ct);
        var offersNewer = newest.Succeeded && (!saved.Succeeded || !newest.HasSameChangesAs(saved));
        return new ProfileSwitchPreview(saved, offersNewer ? newest : null);
    }

    /// <summary>
    /// Applies an immutable saved profile snapshot, then activates its name and saves the profile as
    /// installed. Exact-version failures leave the old installation/profile intact; activation failures
    /// retain a retryable marker.
    /// </summary>
    /// <param name="useNewerVersions">Install the newest versions that work (never older than saved) rather than the saved ones.</param>
    /// <param name="expected">
    /// The plan the user confirmed. The switch is planned again under the reservation with the same
    /// choice; if it differs, <see cref="PlanChangedException"/> is thrown before anything changes.
    /// Null skips the comparison.
    /// </param>
    /// <exception cref="InvalidProfileException">The profile's name is empty or too long.</exception>
    public async Task SwitchAsync(GameInstall game, ModProfile profile, ModResolutionService resolution,
        IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default,
        bool useNewerVersions = false, ModChangePlan? expected = null)
    {
        var snapshot = Snapshot(profile);
        await installs.ApplyProfileAsync(game, async () =>
        {
            // Without an active profile the installed mods are saved nowhere, and switching would
            // remove them for good. Every change is recorded into the active profile, so with one
            // the outgoing mods are already saved.
            if (await HasUnsavedModsAsync(game, ct))
            {
                return ModChangePlan.Failed([UNSAVED_MODS_PROBLEM]);
            }
            var plan = await resolution.PlanProfileAsync(game, snapshot, useNewerVersions, ct);
            if (expected is not null && plan.Succeeded && !plan.HasSameChangesAs(expected))
            {
                throw new PlanChangedException(plan);
            }
            return plan;
        },
            snapshot.Name, name => Activate(game, name), progress, ct);
    }

    /// <summary>Completes committed activation before any caller can record installed mods into a profile.</summary>
    public Task ReconcileAsync(GameInstall game) => Task.Run(() =>
        installs.InstallerFor(game).CompleteProfileActivation(name => Activate(game, name)));

    public string? ProblemWithNewName(string? name) => store.ProblemWithNewName(name);

    /// <summary>Forgets the game's active profile; for a server that's being removed. Saved profiles are kept.</summary>
    public Task ForgetAsync(GameInstall game) => Task.Run(() => store.ClearActive(game.GamePath));

    /// <summary>Mods another manager installed that the user hasn't chosen to leave alone.</summary>
    public async Task<FoundMods> FindUnmanagedAsync(GameInstall game, CancellationToken ct = default)
    {
        var found = await installs.FindUnmanagedAsync(game, ct);
        var leftAlone = await Task.Run(() => store.LeftAlone(game.GamePath), ct);
        return found with { Mods = found.Mods.Where(m => !leftAlone.Contains(m.Location)).ToList() };
    }

    /// <summary>
    /// Rewrites the active profile as what ADA has installed, keeping entries this install doesn't
    /// have unless <paramref name="removed"/> names them: a client-only mod in a profile shared with a
    /// server stays in it when the server records. Called after every change. Does nothing while the
    /// game has no profile: <see cref="CreateAsync"/> captures the mods then.
    /// </summary>
    /// <param name="removed">Mods this change uninstalled, which leave the profile; null when it removed none.</param>
    public async Task RecordInstalledAsync(GameInstall game, IReadOnlyCollection<string>? removed = null)
    {
        await ReconcileAsync(game);
        await Task.Run(() =>
        {
            var installer = installs.InstallerFor(game);
            installer.ThrowIfUnsettled("recording this profile");
            if (store.ActiveName(game.GamePath) is { } name)
            {
                SaveAsInstalled(installer, name, removed ?? []);
            }
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
        {
            throw new InvalidProfileException(problem);
        }

        if (switchTo)
        {
            return await AdoptIntoAsync(game, found, name, ct);
        }

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

    /// <summary>
    /// Takes the found mods over and records them in a profile, holding the installation from the
    /// first read to the last write so no other operation sees adopted mods missing from records.
    /// </summary>
    /// <param name="switchTo">A new profile to make active once the mods are taken over; null for the current one.</param>
    /// <exception cref="InvalidProfileException">No new profile was given and the game has none yet.</exception>
    private async Task<FoundModsImport> AdoptIntoAsync(GameInstall game, IReadOnlyList<FoundMod> found, string? switchTo,
        CancellationToken ct)
    {
        using var operation = installs.BeginOperation(game);
        if (switchTo is null && await ActiveNameAsync(game) is null)
        {
            throw new InvalidProfileException("Create a profile for this installation first, or import the mods into a new one.");
        }

        // Read before adopting: with no record yet, every installed mod counts as asked for, which
        // would otherwise include the dependencies just taken over.
        var installer = installs.InstallerFor(game);
        var requestedBefore = await Task.Run(() => installer.Requested, ct);
        var adopted = await installs.AdoptReservedAsync(game, found, ct);
        return await Task.Run(() =>
        {

            // What a found mod's .uplugin requires is a dependency, not something the user picked.
            var required = found.Where(f => adopted.Any(a => a.Reference == f.Reference))
                .SelectMany(f => f.Dependencies)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var picked = adopted.Select(a => a.Reference).Where(r => !required.Contains(r));
            installer.SaveRequested(requestedBefore.Concat(picked));

            var name = switchTo ?? store.ActiveName(game.GamePath)!;
            var profile = new ModProfile(name, ToProfileMods(installer.Installed.Values, out var skipped));
            store.Save(profile);
            if (switchTo is not null)
            {
                store.SetActive(game.GamePath, switchTo);
            }

            _log.LogInformation("Took over {Count} found mods in {Game} into profile {Profile}", adopted.Count, game.GamePath, name);
            return new FoundModsImport(profile, adopted, skipped);
        }, ct);
    }

    /// <summary>
    /// Makes <paramref name="name"/> the game's active profile and saves it as installed, so versions
    /// chosen during a switch (e.g. newer ones) are kept. Runs under the game's lock during activation,
    /// and again on a retry after a crash; both writes are idempotent.
    /// </summary>
    private void Activate(GameInstall game, string name)
    {
        store.SetActive(game.GamePath, name);
        SaveAsInstalled(installs.InstallerFor(game), name, []);
    }

    /// <summary>
    /// Saves the profile as what's installed, plus its entries this install doesn't have (mods it
    /// has no build of and doesn't need, and their libraries), except those in <paramref name="removed"/>.
    /// Without that, a server sharing a profile would drop its client-only mods from it.
    /// </summary>
    private void SaveAsInstalled(ModInstaller installer, string name, IReadOnlyCollection<string> removed)
    {
        var installed = installer.Installed;
        var gone = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
        var kept = (store.Get(name)?.Mods ?? [])
            .Where(m => !installed.ContainsKey(m.Name) && !gone.Contains(m.Name));
        store.Save(new ModProfile(name, [.. ToProfileMods(installed.Values, out _), .. kept]));
    }

    /// <summary>The game has managed mods but no active profile to keep them in.</summary>
    private Task<bool> HasUnsavedModsAsync(GameInstall game, CancellationToken ct) => Task.Run(() =>
        store.ActiveName(game.GamePath) is null && installs.InstallerFor(game).Installed.Count > 0, ct);

    /// <exception cref="InvalidProfileException">The name is empty or too long.</exception>
    private static ModProfile Snapshot(ModProfile profile)
    {
        var snapshot = profile with { Mods = profile.Mods.ToArray() };
        if (string.IsNullOrWhiteSpace(snapshot.Name) || snapshot.Name.Length > OlpFile.MAX_NAME_LENGTH)
        {
            throw new InvalidProfileException("The profile name is empty or too long.");
        }
        return snapshot;
    }

    private static List<ProfileMod> ToProfileMods(IEnumerable<InstalledMod> installed, out IReadOnlyList<string> skipped)
    {
        var mods = installed.Select(m => new ProfileMod(m.Reference, m.Version)).ToList();
        skipped = mods.Where(m => !ModIdentifiers.IsExactVersion(m.Version)).Select(m => m.Name).ToList();
        return mods.Where(m => ModIdentifiers.IsExactVersion(m.Version)).ToList();
    }
}
