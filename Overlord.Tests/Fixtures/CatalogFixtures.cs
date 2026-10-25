using Overlord.Core.Catalog;

namespace Overlord.Tests.Fixtures;

public static class CatalogFixtures
{
    public static CatalogMod Mod(
        string reference,
        string? name = null,
        string shortDescription = "",
        string? fullDescription = null,
        int popularity = 0,
        DateTimeOffset? updatedAt = null,
        string[]? authors = null,
        string[]? tags = null,
        DateTimeOffset? lastVersion = null) =>
        new(
            Id: "id-" + reference,
            Reference: reference,
            Name: name ?? reference,
            ShortDescription: shortDescription,
            FullDescription: fullDescription,
            LogoUrl: null,
            Authors: authors ?? [],
            Tags: tags ?? [],
            Downloads: 0,
            Popularity: popularity,
            Hotness: 0,
            UpdatedAt: updatedAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastVersionDate: lastVersion,
            CompatibilityRelease: CompatibilityState.Works,
            CompatibilityExperimental: CompatibilityState.Unknown);

    public static CatalogVersion Version(string reference, string version) =>
        new("v-" + version, reference, version, ">=1", VersionStability.Release, false,
            DateTimeOffset.UnixEpoch, [new VersionTarget(TargetPlatform.Windows, "/dl", 10, "abc")], []);
}

/// <summary>A clock tests can move.</summary>
public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
