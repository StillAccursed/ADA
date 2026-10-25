using Overlord.Infrastructure.Catalog;
using Overlord.Tests.Fixtures;
using static Overlord.Tests.Fixtures.CatalogFixtures;

namespace Overlord.Tests.Catalog;

public sealed class ModCatalogStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly ModCatalogStore _store;

    public ModCatalogStoreTests() => _store = new ModCatalogStore(_dir.Combine("catalog.db"));

    public void Dispose() => _dir.Dispose();

    private string[] SearchRefs(string query) => _store.Search(query).Select(m => m.Reference).ToArray();

    [Fact]
    public void Round_trips_a_mod()
    {
        var mod = Mod("WeatherExtended", "Weather Extended", "Darker nights", authors: ["59thCasper"], tags: ["Visual"]);
        _store.Upsert([mod], T0);

        Assert.Equivalent(mod, _store.Get("weatherextended"), strict: true);
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void Prefix_search_across_words_and_fields()
    {
        _store.Upsert(
        [
            Mod("WeatherExtended", "Weather Extended", "Dense fog and meteor showers"),
            Mod("BeltMk6", "Conveyor Belt Mk.6", "Faster belts"),
            Mod("Other", "Something else", authors: ["Casper"]),
        ], T0);

        Assert.Equal(["BeltMk6"], SearchRefs("conv bel"));
        Assert.Equal(["WeatherExtended"], SearchRefs("METEOR"));
        Assert.Equal(["Other"], SearchRefs("casp"));
        Assert.Empty(SearchRefs("weather belt"));
    }

    [Fact]
    public void Finds_mods_by_split_reference_and_image_text()
    {
        _store.Upsert(
        [
            Mod("PowerSuitModule", "PSM", fullDescription: "Intro\n![Glowing reactor core](https://cdn/x.png)"),
            Mod("Pics", "Pics", fullDescription: """<img src="https://cdn/hover-pack-preview.webp" alt="">"""),
        ], T0);

        Assert.Equal(["PowerSuitModule"], SearchRefs("suit"));
        Assert.Equal(["PowerSuitModule"], SearchRefs("reactor"));
        Assert.Equal(["Pics"], SearchRefs("hover pack"));
        Assert.Empty(SearchRefs("cdn")); // URLs are not indexed as text
    }

    [Fact]
    public void Exact_name_ranks_first_then_relevance_then_popularity()
    {
        _store.Upsert(
        [
            Mod("A", "Storage Plus", "storage storage storage", popularity: 1),
            Mod("B", "Storage", popularity: 0),
            Mod("C", "Tiny", "mentions storage once", popularity: 99),
        ], T0);

        Assert.Equal(["B", "A", "C"], SearchRefs("storage"));
    }

    [Fact]
    public void Empty_query_lists_most_popular()
    {
        _store.Upsert([Mod("Low", popularity: 1), Mod("High", popularity: 9)], T0);

        Assert.Equal(["High", "Low"], SearchRefs("  "));
    }

    [Fact]
    public void Updating_a_mod_replaces_its_search_entry()
    {
        _store.Upsert([Mod("M", "Old Name")], T0);
        _store.Upsert([Mod("M", "New Name")], T0);

        Assert.Empty(SearchRefs("old"));
        Assert.Equal(["M"], SearchRefs("new"));
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void Deletes_mods_not_seen_since()
    {
        _store.Upsert([Mod("Gone")], T0);
        _store.Upsert([Mod("Kept")], T0.AddDays(7));

        Assert.Equal(1, _store.DeleteNotSeenSince(T0.AddDays(7)));
        Assert.Null(_store.Get("Gone"));
        Assert.Empty(SearchRefs("gone"));
        Assert.NotNull(_store.Get("Kept"));
    }

    [Fact]
    public void Version_cache_expires_and_is_cleared_by_a_new_release()
    {
        _store.Upsert([Mod("M", lastVersion: T0.AddDays(-3))], T0);
        _store.SaveVersions("M", [Version("M", "1.0.0")], T0);

        var cached = _store.GetVersions("M", T0.AddHours(-1));
        Assert.Equal("1.0.0", Assert.Single(cached!).Version);
        Assert.Equal("abc", cached![0].Targets[0].Hash);
        Assert.Null(_store.GetVersions("M", T0.AddHours(1)));

        // Other changes (name, stats, ficsit.app's constantly-bumped updated_at) keep the cache.
        _store.Upsert([Mod("M", "Renamed", updatedAt: T0, lastVersion: T0.AddDays(-3))], T0);
        Assert.NotNull(_store.GetVersions("M", T0.AddHours(-1)));

        _store.Upsert([Mod("M", "Renamed", lastVersion: T0)], T0);
        Assert.Null(_store.GetVersions("M", T0.AddHours(-1)));
    }

    [Fact]
    public void Survives_reopening()
    {
        _store.Upsert([Mod("M")], T0);
        _store.SetState("k", T0);

        var reopened = new ModCatalogStore(_dir.Combine("catalog.db"));

        Assert.Equal(["M"], reopened.Search("m").Select(m => m.Reference));
        Assert.Equal(T0, reopened.GetState("k"));
    }
}
