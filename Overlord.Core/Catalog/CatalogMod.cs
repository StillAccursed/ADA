namespace Overlord.Core.Catalog;

/// <summary>Community-reported state of a mod on a game branch.</summary>
public enum CompatibilityState
{
    Unknown,
    Works,
    Damaged,
    Broken,
}

/// <summary>A mod as listed on ficsit.app, without its versions (those are fetched on demand).</summary>
/// <param name="Reference">Stable identifier, e.g. <c>WeatherExtended</c>; also the install folder name.</param>
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
    CompatibilityState CompatibilityExperimental);
