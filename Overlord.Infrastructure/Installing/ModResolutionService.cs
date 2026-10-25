using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Overlord.Core.Catalog;
using Overlord.Core.Games;
using Overlord.Core.Resolution;

namespace Overlord.Infrastructure.Installing;

/// <param name="InstalledVersion">The version this replaces, or null for a new mod.</param>
public sealed record PlannedInstall(ResolvedMod Mod, string? InstalledVersion)
{
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
public sealed record ModChangePlan(
    IReadOnlyList<string> Requested,
    IReadOnlyList<PlannedInstall> Install,
    IReadOnlyList<string> Uninstall,
    long DownloadBytes,
    IReadOnlyList<string> Problems,
    bool RequestedChanged = false)
{
    public bool Succeeded => Problems.Count == 0;

    public bool ChangesNothing => Install.Count == 0 && Uninstall.Count == 0 && !RequestedChanged;

    internal static ModChangePlan Failed(IReadOnlyList<string> problems) => new([], [], [], 0, problems);
}

/// <summary>
/// Works out which version of every mod (dependencies and SML included) to install in one game,
/// using that game's build and platform, and keeping what is already installed where it still fits.
/// </summary>
public sealed class ModResolutionService(
    Func<string, CancellationToken, Task<IReadOnlyList<CatalogVersion>>> getVersions,
    ModInstallService installs,
    ILogger<ModResolutionService>? log = null)
{
    private readonly ILogger _log = log ?? NullLogger<ModResolutionService>.Instance;
    private readonly DependencyResolver _resolver = new(getVersions);

    /// <param name="preferred">
    /// Versions to keep where they fit (e.g. a profile's lockfile). Defaults to what Overlord has
    /// installed in the game, so a resolve changes as little as possible.
    /// </param>
    public async Task<ResolveResult> ResolveAsync(GameInstall game, IReadOnlyList<ModRequirement> requested,
        IReadOnlyDictionary<string, string>? preferred = null, CancellationToken ct = default)
    {
        if (game.Build is not { } build)
        {
            return new ResolveResult([],
                ["Overlord couldn't read this game's build number, so it can't tell which mod versions are compatible. Try verifying the game's files in its launcher."]);
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
        PlanAsync(game, requested => requested.Add(reference), reference, removing: false, ct);

    /// <summary>
    /// Plans removing a mod, plus any dependency only it needed. Refused while another installed mod
    /// still needs it.
    /// </summary>
    public Task<ModChangePlan> PlanUninstallAsync(GameInstall game, string reference, CancellationToken ct = default) =>
        PlanAsync(game, requested => requested.Remove(reference), reference, removing: true, ct);

    /// <summary>
    /// Applies a plan in one transaction and records what the user asked for. Additions are recorded
    /// before the install and removals after it, so a crash part way never makes a wanted mod look unneeded.
    /// </summary>
    /// <inheritdoc cref="ModInstallService.InstallAsync" path="/exception"/>
    public async Task<TransactionResult> ApplyAsync(GameInstall game, ModChangePlan plan,
        IProgress<ModInstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (!plan.Succeeded)
            throw new InvalidOperationException("Can't apply a plan that has problems.");

        var installer = installs.InstallerFor(game);
        await Task.Run(() => installer.SaveRequested(installer.Requested.Concat(plan.Requested)), ct);

        // Only the record changes (a dependency is now asked for): no transaction needed.
        var result = plan.Install.Count == 0 && plan.Uninstall.Count == 0
            ? new TransactionResult([], [])
            : await installs.ApplyAsync(game, plan.Install.Select(i => i.Mod.Version).ToList(), plan.Uninstall, progress, ct);

        await Task.Run(() => installer.SaveRequested(plan.Requested), CancellationToken.None);
        return result;
    }

    private async Task<ModChangePlan> PlanAsync(GameInstall game, Action<HashSet<string>> change, string reference,
        bool removing, CancellationToken ct)
    {
        var (installed, requested) = await Task.Run(() =>
        {
            var installer = installs.InstallerFor(game);
            return (installer.Installed, installer.Requested.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }, ct);
        var before = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (removing && !installed.ContainsKey(reference))
            return ModChangePlan.Failed([$"{reference} isn't installed by Overlord in this game."]);

        change(requested);
        var resolved = await ResolveAsync(game, requested.Select(r => new ModRequirement(r)).ToList(), null, ct);
        if (!resolved.Succeeded)
            return ModChangePlan.Failed(resolved.Problems);

        var chosen = resolved.Mods.ToDictionary(m => m.Reference, StringComparer.OrdinalIgnoreCase);
        if (removing && chosen.TryGetValue(reference, out var stillNeeded))
        {
            return ModChangePlan.Failed(
                [$"{reference} can't be removed while {string.Join(", ", stillNeeded.RequiredBy)} need{(stillNeeded.RequiredBy.Count == 1 ? "s" : "")} it."]);
        }

        var platform = ModInstallService.TargetFor(game);

        // The installer refuses these too, but only after downloading; say so before anything starts.
        var foreign = resolved.Mods
            .Where(m => !installed.ContainsKey(m.Reference)
                        && (Directory.Exists(Path.Combine(game.ModsPath, m.Reference))
                            || Directory.Exists(Path.Combine(game.ModsPath, "GameFeatures", m.Reference))))
            .Select(m => m.Reference)
            .ToList();
        if (foreign.Count > 0)
        {
            return ModChangePlan.Failed(
                [$"{string.Join(", ", foreign)} {(foreign.Count == 1 ? "is" : "are")} already in the Mods folder, but not installed by Overlord (perhaps by another mod manager). Remove {(foreign.Count == 1 ? "it" : "them")} from the Mods folder first, then try again."]);
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
            !before.SetEquals(requested));
    }
}
