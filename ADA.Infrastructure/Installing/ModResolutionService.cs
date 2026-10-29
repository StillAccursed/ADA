using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Profiles;
using ADA.Core.Resolution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace ADA.Infrastructure.Installing;

/// <param name="InstalledVersion">The version this replaces, or null for a new mod.</param>
public sealed record PlannedInstall(ResolvedMod Mod, string? InstalledVersion)
{
    public bool IsUpdate => InstalledVersion is not null;

    public string Reference => Mod.Reference;

    public string Version => Mod.Version.Version;
}

/// <summary>What installing or uninstalling one mod changes in a game, worked out before anything is touched.</summary>
/// <param name="Requested">The mods the user will have asked for once this is applied.</param>
/// <param name="Install">New and changed mods, dependencies first.</param>
/// <param name="Uninstall">Mods to remove: the one asked for, and dependencies nothing needs any more.</param>
/// <param name="DownloadBytes">Total size of the builds to install, as published (kept archives may cover some).</param>
/// <param name="Problems">Why it can't be done, in plain language. Empty when it can.</param>
/// <param name="RequestedChanged">
/// Whether the set of mods the user asked for changes, e.g. asking for a mod already installed as a
/// dependency, so it stays when nothing else needs it.
/// </param>
/// <param name="Skipped">
/// Mods left out because the game's platform has no build and doesn't need one (a client-only mod
/// on a server). They stay requested, so they aren't changes, and never affect the comparisons below.
/// </param>
public sealed record ModChangePlan(
    IReadOnlyList<string> Requested,
    IReadOnlyList<PlannedInstall> Install,
    IReadOnlyList<string> Uninstall,
    long DownloadBytes,
    IReadOnlyList<string> Problems,
    bool RequestedChanged = false,
    IReadOnlyList<SkippedMod>? Skipped = null)
{
    public bool Succeeded => Problems.Count == 0;

    public IReadOnlyList<SkippedMod> Skipped { get; init; } = Skipped ?? [];

    /// <summary>
    /// Removals that also leave the active profile: everything in <see cref="Uninstall"/> except mods
    /// removed only because this platform skips them (a client-only mod on a server), which other
    /// installs sharing the profile still use.
    /// </summary>
    public IReadOnlyList<string> LeavesProfile => Uninstall
        .Where(r => !Skipped.Any(s => s.Reference.Equals(r, StringComparison.OrdinalIgnoreCase)))
        .ToList();

    public bool ChangesNothing => Install.Count == 0 && Uninstall.Count == 0 && !RequestedChanged;

    /// <summary>
    /// Whether <paramref name="other"/> makes exactly the same changes: the same mods installed at
    /// the same versions, the same mods removed and the same requested set, ignoring order and case.
    /// Download size is not compared, as kept archives change it without changing any mod.
    /// </summary>
    public bool HasSameChangesAs(ModChangePlan other) =>
        SameSet(Install.Select(i => $"{i.Reference}@{i.Version}"), other.Install.Select(i => $"{i.Reference}@{i.Version}"))
        && SameSet(Uninstall, other.Uninstall)
        && SameSet(Requested, other.Requested);

    internal static ModChangePlan Failed(IReadOnlyList<string> problems) => new([], [], [], 0, problems);

    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) =>
        a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);
}

/// <summary>
/// Works out which version of every mod (dependencies and SML included) to install in one game,
/// using that game's build and platform, and keeping what is already installed where it still fits.
/// </summary>
public sealed class ModResolutionService(
    IModVersionSource versions,
    ModInstallService installs,
    ILogger<ModResolutionService>? log = null)
{
    private readonly ILogger _log = log ?? NullLogger<ModResolutionService>.Instance;
    private readonly DependencyResolver _resolver = new(versions);

    /// <param name="preferred">
    /// Versions to keep where they fit (e.g. a profile's lockfile). Defaults to what ADA has
    /// installed in the game, so a resolve changes as little as possible.
    /// </param>
    public async Task<ResolveResult> ResolveAsync(GameInstall game, IReadOnlyList<ModRequirement> requested,
        IReadOnlyDictionary<string, string>? preferred = null, CancellationToken ct = default)
    {
        if (game.Build is not { } build)
        {
            return new ResolveResult([],
                ["ADA couldn't read this game's build number, so it can't tell which mod versions are compatible. Try verifying the game's files in its launcher."]);
        }

        preferred ??= await Task.Run(() => installs.InstallerFor(game).Installed.Values
            .ToDictionary(m => m.Reference, m => m.Version, StringComparer.OrdinalIgnoreCase), ct);

        var request = new ResolveRequest(requested, build.Changelist, ModInstallService.TargetFor(game), preferred);
        var result = await _resolver.ResolveAsync(request, ct);

        if (result.Succeeded)
        {
            _log.LogInformation("Resolved {Requested} for game build {Build}: {Mods}",
                requested.Select(r => r.Reference), build.Changelist,
                result.Mods.Select(m => $"{m.Reference} {m.Version.Version}"));
        }
        else
        {
            _log.LogWarning("Couldn't resolve {Requested} for game build {Build}: {Problems}",
                requested.Select(r => r.Reference), build.Changelist, result.Problems);
        }
        return result;
    }

    /// <summary>Plans adding a mod (and whatever it needs) to the mods the user asked for in this game.</summary>
    public Task<ModChangePlan> PlanInstallAsync(GameInstall game, string reference, CancellationToken ct = default) =>
        PlanInstallAsync(game, [reference], ct);

    /// <summary>Plans adding several mods at once (the Mods page's queue), so they install in one transaction.</summary>
    public Task<ModChangePlan> PlanInstallAsync(GameInstall game, IReadOnlyCollection<string> references, CancellationToken ct = default) =>
        PlanAsync(game, requested => requested.UnionWith(references), "", removing: false, ct);

    /// <summary>
    /// Plans installing one exact version of a mod (picked on its mod page), or switching to it. Later
    /// plans keep it while it fits; Check for Updates still offers newer ones.
    /// </summary>
    public Task<ModChangePlan> PlanInstallVersionAsync(GameInstall game, string reference, string version, CancellationToken ct = default) =>
        PlanAsync(game, requested => requested.Add(reference), "", removing: false, ct,
            ranges: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [reference] = version });

    /// <summary>Plans the complete saved profile; unmanaged collisions are refused before downloads.</summary>
    /// <param name="newest">
    /// False: every profile mod at exactly its saved version. True: the newest versions that work
    /// together with this game, each at least its saved version (a constraint, so never a downgrade).
    /// Dependencies outside the profile are resolved either way.
    /// </param>
    internal async Task<ModChangePlan> PlanProfileAsync(GameInstall game, ModProfile profile, bool newest = false,
        CancellationToken ct = default)
    {
        if (profile.Mods.Any(m => !ModIdentifiers.IsValidReference(m.Name) || !ModIdentifiers.IsExactVersion(m.Version))
            || profile.Mods.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profile.Mods.Count)
        {
            return ModChangePlan.Failed(["The profile contains invalid or duplicate mods."]);
        }
        var saved = profile.Mods.ToDictionary(m => m.Name, m => m.Version, StringComparer.OrdinalIgnoreCase);
        var ranges = newest
            ? saved.ToDictionary(m => m.Key, m => $">={m.Value}", StringComparer.OrdinalIgnoreCase)
            : saved;
        IReadOnlyDictionary<string, string> preferred = newest ? new Dictionary<string, string>() : saved;
        return await PlanAsync(game, requested =>
        {
            requested.Clear();
            requested.UnionWith(saved.Keys);
        }, "", removing: false, ct, preferred, ranges);
    }

    /// <summary>
    /// Plans removing a mod, plus any dependency only it needed. Refused while another installed mod
    /// still needs it.
    /// </summary>
    public Task<ModChangePlan> PlanUninstallAsync(GameInstall game, string reference, CancellationToken ct = default) =>
        PlanAsync(game, requested => requested.Remove(reference), reference, removing: true, ct);

    /// <summary>
    /// Plans what the installed mods need to work in this game: missing dependencies (e.g. SML after
    /// importing mods from another manager), and newer versions where an installed one doesn't fit
    /// the game build or another mod. Never removes anything.
    /// </summary>
    public async Task<ModChangePlan> PlanFixAsync(GameInstall game, CancellationToken ct = default)
    {
        var plan = await PlanAsync(game, _ => { }, "", removing: false, ct);
        return plan with { Uninstall = [] };
    }

    /// <summary>
    /// Plans updating every installed mod to the newest versions that work together in this game
    /// (release over beta, as always). Never removes anything.
    /// </summary>
    /// <param name="only">Update just this mod (and whatever its new version needs), keeping the rest where they fit.</param>
    public async Task<ModChangePlan> PlanUpdateAsync(GameInstall game, string? only = null, CancellationToken ct = default)
    {
        var keep = only is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : (await Task.Run(() => installs.InstallerFor(game).Installed, ct)).Values
                .Where(m => !string.Equals(m.Reference, only, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(m => m.Reference, m => m.Version, StringComparer.OrdinalIgnoreCase);
        var plan = await PlanAsync(game, _ => { }, "", removing: false, ct, keep);

        // Release over beta can pick an older version than an installed beta: keep those as they are.
        var older = plan.Install
            .Where(i => i.IsUpdate && IsOlder(i.Version, i.InstalledVersion!))
            .ToDictionary(i => i.Reference, i => i.InstalledVersion!, StringComparer.OrdinalIgnoreCase);
        if (older.Count > 0)
        {
            foreach (var (reference, version) in older)
            {
                keep[reference] = version;
            }

            plan = await PlanAsync(game, _ => { }, "", removing: false, ct, keep);
        }
        return plan with { Uninstall = [] };
    }

    /// <summary>
    /// Applies a plan in one transaction and records what the user asked for. Additions are recorded
    /// before the install and removals after it, so a crash part way never makes a wanted mod look unneeded.
    /// </summary>
    /// <inheritdoc cref="ModInstallService.InstallAsync" path="/exception"/>
    public async Task<TransactionResult> ApplyAsync(GameInstall game, ModChangePlan plan,
        IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (!plan.Succeeded)
        {
            throw new InvalidOperationException("Can't apply a plan that has problems.");
        }

        using var operation = installs.BeginOperation(game);
        var installer = installs.InstallerFor(game);
        installer.ThrowIfUnsettled("changing mods");
        await Task.Run(() => installer.SaveRequested(installer.Requested.Concat(plan.Requested)), ct);

        // Only the record changes (a dependency is now asked for): no transaction needed.
        var result = plan.Install.Count == 0 && plan.Uninstall.Count == 0
            ? new TransactionResult([], [])
            : await installs.ApplyReservedAsync(game, plan.Install.Select(i => i.Mod.Version).ToList(), plan.Uninstall, progress, ct);

        await Task.Run(() => installer.SaveRequested(plan.Requested), CancellationToken.None);
        return result;
    }

    /// <param name="preferred">Versions to keep while they fit; null keeps what is installed.</param>
    /// <param name="ranges">Ranges for some requested mods, e.g. one exact version; the rest take any version.</param>
    private async Task<ModChangePlan> PlanAsync(GameInstall game, Action<HashSet<string>> change, string reference,
        bool removing, CancellationToken ct, IReadOnlyDictionary<string, string>? preferred = null,
        IReadOnlyDictionary<string, string>? ranges = null)
    {
        var (installed, requested) = await Task.Run(() =>
        {
            var installer = installs.InstallerFor(game);
            return (installer.Installed, installer.Requested.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }, ct);
        var before = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (removing && !installed.ContainsKey(reference))
        {
            return ModChangePlan.Failed([$"{reference} isn't installed by ADA in this game."]);
        }

        change(requested);
        var resolved = await ResolveAsync(game, requested.Select(r => new ModRequirement(r, ranges?.GetValueOrDefault(r))).ToList(),
            preferred, ct);
        if (!resolved.Succeeded)
        {
            return ModChangePlan.Failed(resolved.Problems);
        }

        var chosen = resolved.Mods.ToDictionary(m => m.Reference, StringComparer.OrdinalIgnoreCase);
        if (removing && chosen.TryGetValue(reference, out var stillNeeded))
        {
            return ModChangePlan.Failed(
                [$"{reference} can't be removed while {string.Join(", ", stillNeeded.RequiredBy)} need{(stillNeeded.RequiredBy.Count == 1 ? "s" : "")} it."]);
        }

        var platform = ModInstallService.TargetFor(game);

        // The installer refuses these too, but only after downloading; say so before anything starts.
        var foreign = await Task.Run(() =>
        {
            var installer = installs.InstallerFor(game);
            return resolved.Mods
                .Where(m => !installed.ContainsKey(m.Reference) && installer.HasModFolder(m.Reference))
                .Select(m => m.Reference)
                .ToList();
        }, ct);
        if (foreign.Count > 0)
        {
            return ModChangePlan.Failed(
                [$"{string.Join(", ", foreign)} {(foreign.Count == 1 ? "is" : "are")} already in the Mods folder, but not installed by ADA (perhaps by another mod manager). Remove {(foreign.Count == 1 ? "it" : "them")} from the Mods folder first, then try again."]);
        }

        var toInstall = resolved.Mods
            .Where(m => !installed.TryGetValue(m.Reference, out var current) || current.Version != m.Version.Version)
            .Select(m => new PlannedInstall(m, installed.GetValueOrDefault(m.Reference)?.Version))
            .ToList();
        var toRemove = installed.Keys
            .Where(r => !chosen.ContainsKey(r))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ModChangePlan(
            [.. requested.Order(StringComparer.OrdinalIgnoreCase)],
            toInstall,
            toRemove,
            toInstall.Sum(i => i.Mod.Version.TargetFor(platform)?.Size ?? 0),
            [],
            !before.SetEquals(requested),
            resolved.Skipped);
    }

    private static bool IsOlder(string version, string than) =>
        SemVersion.TryParse(version, SemVersionStyles.Any, out var a)
        && SemVersion.TryParse(than, SemVersionStyles.Any, out var b)
        && SemVersion.ComparePrecedence(a, b) < 0;
}
