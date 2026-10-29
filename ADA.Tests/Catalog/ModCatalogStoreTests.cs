using ADA.Core.Catalog;
using ADA.Infrastructure.Catalog;
using ADA.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using static ADA.Tests.Fixtures.CatalogFixtures;

namespace ADA.Tests.Catalog;

public sealed class ModCatalogStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly ModCatalogStore _store;

    public ModCatalogStoreTests() => _store = new ModCatalogStore(_dir.Combine("catalog.db"));

    public void Dispose() => _dir.Dispose();

    // Same Id each time, so the second Upsert updates the existing row.
    private static CatalogMod FullMod(int seed) => new(
        Id: "id-full",
        Reference: $"FullMod{seed}",
        Name: $"Full Mod {seed}",
        ShortDescription: $"short {seed}",
        FullDescription: $"long {seed}",
        LogoUrl: $"https://cdn/logo{seed}.png",
        Authors: [$"author{seed}a", $"author{seed}b"],
        Tags: [$"tag{seed}"],
        Downloads: 100 + seed,
        Popularity: 200 + seed,
        Hotness: 300 + seed,
        UpdatedAt: T0.AddDays(seed),
        LastVersionDate: T0.AddHours(seed),
        CompatibilityRelease: seed == 1 ? CompatibilityState.Works : CompatibilityState.Damaged,
        CompatibilityExperimental: seed == 1 ? CompatibilityState.Broken : CompatibilityState.Unknown,
        Views: 400 + seed,
        Latest: [new LatestVersion(VersionStability.Beta, $"{seed}.2.3", $">={seed}", [TargetPlatform.Windows, TargetPlatform.LinuxServer])]);

    private string[] SearchRefs(string query) => _store.Search(query).Select(m => m.Reference).ToArray();

    [Fact]
    public void RoundTripsAMod()
    {
        var mod = Mod("WeatherExtended", "Weather Extended", "Darker nights", authors: ["59thCasper"], tags: ["Visual"]);
        _store.Upsert([mod], T0);

        Assert.Equivalent(mod, _store.Get("weatherextended"), strict: true);
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void EveryFieldSurvivesInsertUpdateAndEachRead()
    {
        // Every value distinct and non-default, so a column swapped with another of the same type
        // (downloads/hotness, the two compat states) or left out of the update shows up as a mismatch.
        var inserted = FullMod(seed: 1);
        var updated = FullMod(seed: 2);

        _store.Upsert([inserted], T0);
        Assert.Equivalent(inserted, _store.Get(inserted.Reference), strict: true);

        _store.Upsert([updated], T0);
        Assert.Equivalent(updated, _store.Get(updated.Reference), strict: true);
        Assert.Equivalent(updated, Assert.Single(_store.Search(updated.Name)), strict: true);
        Assert.Equivalent(updated, Assert.Single(_store.GetRecentlyUpdated(10)), strict: true);
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void PrefixSearchAcrossWordsAndFields()
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
    public void FindsModsBySplitReferenceAndImageText()
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
    public void ExactNameRanksFirstThenRelevanceThenPopularity()
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
    public void EmptyQueryListsMostPopular()
    {
        _store.Upsert([Mod("Low", popularity: 1), Mod("High", popularity: 9)], T0);

        Assert.Equal(["High", "Low"], SearchRefs("  "));
    }

    [Fact]
    public void UpdatingAModReplacesItsSearchEntry()
    {
        _store.Upsert([Mod("M", "Old Name")], T0);
        _store.Upsert([Mod("M", "New Name")], T0);

        Assert.Empty(SearchRefs("old"));
        Assert.Equal(["M"], SearchRefs("new"));
        Assert.Equal(1, _store.Count());
    }

    [Fact]
    public void DeletesModsNotSeenSince()
    {
        _store.Upsert([Mod("Gone")], T0);
        _store.Upsert([Mod("Kept")], T0.AddDays(7));

        Assert.Equal(1, _store.DeleteNotSeenSince(T0.AddDays(7)));
        Assert.Null(_store.Get("Gone"));
        Assert.Empty(SearchRefs("gone"));
        Assert.NotNull(_store.Get("Kept"));
    }

    [Fact]
    public void RecentlyUpdatedSortsReleasesNotMetadataEditsAndAppliesTheLimitAfterSorting()
    {
        var now = DateTimeOffset.UtcNow;
        _store.Upsert([
            Mod("Edited", updatedAt: now, lastVersion: now.AddDays(-7)),
            Mod("Newest", updatedAt: now.AddDays(-2), lastVersion: now),
            Mod("Undated", updatedAt: now),
            Mod("TieB", "Beta", lastVersion: now.AddDays(-1)),
            Mod("TieA", "Alpha", lastVersion: now.AddDays(-1)),
        ], now);
        Assert.Equal(["Newest", "TieA", "TieB"], _store.GetRecentlyUpdated(3).Select(m => m.Reference));
        Assert.Equal(["Newest", "TieA", "TieB", "Edited"], _store.GetRecentlyUpdated(20).Select(m => m.Reference));
    }

    [Fact]
    public void VersionCacheExpiresAndIsClearedByANewRelease()
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
    public void SurvivesReopening()
    {
        _store.Upsert([Mod("M")], T0);
        _store.SetState("k", T0);

        var reopened = new ModCatalogStore(_dir.Combine("catalog.db"));

        Assert.Equal(["M"], reopened.Search("m").Select(m => m.Reference));
        Assert.Equal(T0, reopened.GetState("k"));
    }

    [Fact]
    public void RebuildsACatalogueFromAnOlderSchema()
    {
        var path = _dir.Combine("old.db");
        using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE mods (rowid INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE);
                INSERT INTO mods (id) VALUES ('stale');
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new ModCatalogStore(path);
        Assert.Equal(0, store.Count());

        store.Upsert([Mod("M")], T0);
        Assert.NotNull(store.Get("M"));
    }
}
