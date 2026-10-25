using Overlord.Core.Catalog;

namespace Overlord.ViewModels;

public enum ModFilter
{
    All,
    Compatible,
    Favourite,
    Installed,
    Queued,
    Dependencies,
    NotInstalled,
}

public enum ModSort
{
    /// <summary>Best search match when searching, otherwise most popular.</summary>
    BestMatch,
    LastUpdated,
    Name,
    Popularity,
    Hotness,
    Views,
    Downloads,
}

public enum ModView
{
    List,
    Grid,
    Card,
}

/// <summary>A choice in one of the Mods page's drop-downs.</summary>
public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <param name="Game">The selected game's build and platform, for <see cref="ModFilter.Compatible"/>; null if unknown.</param>
/// <param name="Requested">Installed mods the user asked for; the rest of <paramref name="Installed"/> are dependencies.</param>
public sealed record ModListContext(
    (int Changelist, TargetPlatform Platform)? Game,
    IReadOnlySet<string> Installed,
    IReadOnlySet<string> Requested,
    IReadOnlySet<string> Favourites,
    IReadOnlySet<string> Queued);

/// <summary>The Mods page's filter, tag matching and sort, over mods already matched by the search box.</summary>
public static class ModListQuery
{
    public static IReadOnlyList<Option<ModFilter>> Filters { get; } =
    [
        new(ModFilter.All, "All"),
        new(ModFilter.Compatible, "Compatible"),
        new(ModFilter.Favourite, "Favourite"),
        new(ModFilter.Installed, "Installed"),
        new(ModFilter.Queued, "Queued"),
        new(ModFilter.Dependencies, "Dependencies"),
        new(ModFilter.NotInstalled, "Not installed"),
    ];

    public static IReadOnlyList<Option<ModSort>> Sorts { get; } =
    [
        new(ModSort.BestMatch, "Best match"),
        new(ModSort.LastUpdated, "Last updated"),
        new(ModSort.Name, "Name"),
        new(ModSort.Popularity, "Popularity"),
        new(ModSort.Hotness, "Hotness"),
        new(ModSort.Views, "Views"),
        new(ModSort.Downloads, "Total downloads"),
    ];

    /// <param name="mods">In search order (best match first, or most popular for an empty search).</param>
    /// <param name="tags">Selected tags; none means any tag.</param>
    /// <param name="matchAllTags">A mod needs every selected tag, rather than at least one.</param>
    public static IEnumerable<CatalogMod> Apply(IEnumerable<CatalogMod> mods, ModFilter filter, IReadOnlyCollection<string> tags,
        bool matchAllTags, ModSort sort, ModListContext context)
    {
        var matching = mods.Where(m => Matches(m, filter, context));
        if (tags.Count > 0)
        {
            matching = matching.Where(m =>
            {
                var own = m.Tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
                return matchAllTags ? tags.All(own.Contains) : tags.Any(own.Contains);
            });
        }

        // OrderBy is stable, so ties keep their search order.
        return sort switch
        {
            ModSort.LastUpdated => matching.OrderByDescending(m => m.LastVersionDate ?? m.UpdatedAt),
            ModSort.Name => matching.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            ModSort.Popularity => matching.OrderByDescending(m => m.Popularity),
            ModSort.Hotness => matching.OrderByDescending(m => m.Hotness),
            ModSort.Views => matching.OrderByDescending(m => m.Views),
            ModSort.Downloads => matching.OrderByDescending(m => m.Downloads),
            _ => matching,
        };
    }

    private static bool Matches(CatalogMod mod, ModFilter filter, ModListContext c) => filter switch
    {
        // Most game ranges are open-ended (>=N), so also leave out mods players report broken.
        ModFilter.Compatible => c.Game is { } game && mod.SupportsGame(game.Changelist, game.Platform)
                                && mod.CompatibilityRelease != CompatibilityState.Broken,
        ModFilter.Favourite => c.Favourites.Contains(mod.Reference),
        ModFilter.Installed => c.Installed.Contains(mod.Reference),
        ModFilter.Queued => c.Queued.Contains(mod.Reference),
        ModFilter.Dependencies => c.Installed.Contains(mod.Reference) && !c.Requested.Contains(mod.Reference),
        ModFilter.NotInstalled => !c.Installed.Contains(mod.Reference),
        _ => true,
    };
}
