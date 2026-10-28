using ADA.Core.Catalog;
using ADA.Tests.Fixtures;
using ADA.ViewModels;

namespace ADA.Tests.ViewModels;

public class ModListQueryTests
{
    private static readonly HashSet<string> NONE = new(StringComparer.OrdinalIgnoreCase);

    private static readonly CatalogMod[] MODS =
    [
        CatalogFixtures.Mod("Alpha", tags: ["Logistics", "QoL"], popularity: 5, views: 900, downloads: 10, hotness: 1,
            lastVersion: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
        CatalogFixtures.Mod("beta", tags: ["QoL"], popularity: 9, views: 100, downloads: 50, hotness: 7,
            lastVersion: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            latest: [new LatestVersion(VersionStability.Release, "2.0.0", ">=520000", [TargetPlatform.Windows])]),
        CatalogFixtures.Mod("Gamma", tags: ["Logistics"], popularity: 1, views: 500, downloads: 99, hotness: 3,
            latest: [new LatestVersion(VersionStability.Release, "1.0.0", ">=513885", [TargetPlatform.WindowsServer])]),
    ];

    private static ModListContext Context(IEnumerable<string>? installed = null, IEnumerable<string>? requested = null,
        IEnumerable<string>? favourites = null, IEnumerable<string>? queued = null) => new(
        (513885, TargetPlatform.Windows),
        (installed ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
        (requested ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
        (favourites ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase),
        (queued ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static string[] Run(ModFilter filter = ModFilter.All, string[]? tags = null, bool all = false,
        ModSort sort = ModSort.BestMatch, ModListContext? context = null) =>
        ModListQuery.Apply(MODS, filter, tags ?? [], all, sort, context ?? Context()).Select(m => m.Reference).ToArray();

    [Fact]
    public void Compatible_means_the_newest_version_supports_this_build_and_platform() =>
        Assert.Equal(["Alpha"], Run(ModFilter.Compatible));

    [Fact]
    public void Compatible_leaves_out_mods_players_report_broken()
    {
        var broken = CatalogFixtures.Mod("Old") with { CompatibilityRelease = CompatibilityState.Broken };

        Assert.Empty(ModListQuery.Apply([broken], ModFilter.Compatible, [], false, ModSort.BestMatch, Context()));
    }

    [Fact]
    public void Installed_dependencies_and_not_installed_split_the_catalogue()
    {
        var context = Context(installed: ["Alpha", "Gamma"], requested: ["Alpha"]);

        Assert.Equal(["Alpha", "Gamma"], Run(ModFilter.Installed, context: context));
        Assert.Equal(["Gamma"], Run(ModFilter.Dependencies, context: context));
        Assert.Equal(["beta"], Run(ModFilter.NotInstalled, context: context));
    }

    [Fact]
    public void Favourite_and_queued_use_their_own_lists()
    {
        var context = Context(favourites: ["gamma"], queued: ["BETA"]);

        Assert.Equal(["Gamma"], Run(ModFilter.Favourite, context: context));
        Assert.Equal(["beta"], Run(ModFilter.Queued, context: context));
    }

    [Fact]
    public void Tags_match_any_or_all()
    {
        Assert.Equal(["Alpha", "beta", "Gamma"], Run(tags: ["Logistics", "QoL"]));
        Assert.Equal(["Alpha"], Run(tags: ["logistics", "qol"], all: true));
    }

    [Theory]
    [InlineData(ModSort.BestMatch, "Alpha,beta,Gamma")]
    [InlineData(ModSort.Name, "Alpha,beta,Gamma")]
    [InlineData(ModSort.LastUpdated, "beta,Alpha,Gamma")]
    [InlineData(ModSort.Popularity, "beta,Alpha,Gamma")]
    [InlineData(ModSort.Hotness, "beta,Gamma,Alpha")]
    [InlineData(ModSort.Views, "Alpha,Gamma,beta")]
    [InlineData(ModSort.Downloads, "Gamma,beta,Alpha")]
    public void Sorts(ModSort sort, string expected) => Assert.Equal(expected.Split(','), Run(sort: sort));
}
