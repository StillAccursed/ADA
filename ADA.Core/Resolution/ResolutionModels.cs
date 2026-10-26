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

/// <param name="Mods">Every mod to install, dependencies before the mods that need them (SML first).</param>
/// <param name="Problems">Why no combination works, in plain language. Empty on success.</param>
public sealed record ResolveResult(IReadOnlyList<ResolvedMod> Mods, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Problems.Count == 0;
}
