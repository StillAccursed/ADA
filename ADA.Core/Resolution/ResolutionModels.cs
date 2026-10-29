using ADA.Core.Catalog;

namespace ADA.Core.Resolution;

/// <summary>A mod the user asked for.</summary>
/// <param name="Range">npm-style semver range (<c>^1.2.0</c>, <c>&gt;=1.0.0</c>, or <c>1.2.3</c> for exactly that version); null for any.</param>
public sealed record ModRequirement(string Reference, string? Range = null);

/// <param name="Requested">What the user asked for; dependencies are added automatically.</param>
/// <param name="GameChangelist">The target game's build (CL); every chosen version must support it.</param>
/// <param name="Platform">Every chosen version must have a build for this platform.</param>
/// <param name="Preferred">
/// Versions to keep when they still fit, e.g. what is installed, or a profile's lockfile, so a
/// resolve changes as little as possible.
/// </param>
public sealed record ResolveRequest(
    IReadOnlyList<ModRequirement> Requested,
    int GameChangelist,
    TargetPlatform Platform,
    IReadOnlyDictionary<string, string>? Preferred = null);

/// <param name="RequiredBy">Why it is included: "requested" and/or the mod versions that depend on it.</param>
public sealed record ResolvedMod(CatalogVersion Version, IReadOnlyList<string> RequiredBy)
{
    public string Reference => Version.ModReference;
}

/// <summary>A mod left out because this platform has no build of it and doesn't need one.</summary>
/// <param name="RequiredBy">Why it was asked for: "requested" and/or the mod versions that depend on it.</param>
public sealed record SkippedMod(string Reference, IReadOnlyList<string> RequiredBy);

/// <param name="Mods">Every mod to install, dependencies before the mods that need them (SML first).</param>
/// <param name="Problems">Why no combination works, in plain language. Empty on success.</param>
/// <param name="Skipped">
/// Mods asked for (directly or as a dependency) that have no build for this platform and aren't
/// needed on it, e.g. a graphics mod on a server. Not installed; empty when nothing was left out.
/// </param>
public sealed record ResolveResult(IReadOnlyList<ResolvedMod> Mods, IReadOnlyList<string> Problems,
    IReadOnlyList<SkippedMod>? Skipped = null)
{
    public bool Succeeded => Problems.Count == 0;

    public IReadOnlyList<SkippedMod> Skipped { get; } = Skipped ?? [];
}
