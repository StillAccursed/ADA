using ADA.Core.Resolution;

namespace ADA.Core.Catalog;

/// <summary>Community-reported state of a mod on a game branch.</summary>
public enum CompatibilityState
{
    Unknown,
    Works,
    Damaged,
    Broken,
}

/// <summary>The newest version of a mod at one stability, as listed with the mod.</summary>
/// <param name="GameVersionRange">Range over the game changelist, e.g. <c>&gt;=513885</c>.</param>
public sealed record LatestVersion(VersionStability Stability, string Version, string GameVersionRange, IReadOnlyList<TargetPlatform> Targets);

/// <summary>A mod as listed on ficsit.app, without its versions (those are fetched on demand).</summary>
/// <param name="Reference">Stable identifier, e.g. <c>WeatherExtended</c>; also the install folder name.</param>
/// <param name="Latest">Newest release, beta and alpha (any may be missing); enough to tell compatibility without fetching every version.</param>
public sealed record CatalogMod(
    string Id,
    string Reference,
    string Name,
    string ShortDescription,
    string? FullDescription,
    string? LogoUrl,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Tags,
    int Downloads,
    int Popularity,
    int Hotness,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastVersionDate,
    CompatibilityState CompatibilityRelease,
    CompatibilityState CompatibilityExperimental,
    int Views = 0,
    IReadOnlyList<LatestVersion>? Latest = null)
{
    /// <summary>
    /// Whether its newest release, beta or alpha supports this game build and platform. Older
    /// versions aren't listed with the mod, so a mod whose newest version needs a newer game counts as not.
    /// </summary>
    public bool SupportsGame(int changelist, TargetPlatform platform) =>
        (Latest ?? []).Any(l => l.Targets.Contains(platform)
                                && GameVersionRange.TryParse(l.GameVersionRange, out var range) && range.Contains(changelist));
}
