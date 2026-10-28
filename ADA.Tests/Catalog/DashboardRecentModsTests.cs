using ADA.Infrastructure.Catalog;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Catalog;

public sealed class DashboardRecentModsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Sorts_releases_not_metadata_edits_and_applies_the_limit_after_sorting()
    {
        var store = new ModCatalogStore(_dir.Combine("catalog.db"));
        var now = DateTimeOffset.UtcNow;
        store.Upsert([
            CatalogFixtures.Mod("Edited", updatedAt: now, lastVersion: now.AddDays(-7)),
            CatalogFixtures.Mod("Newest", updatedAt: now.AddDays(-2), lastVersion: now),
            CatalogFixtures.Mod("Undated", updatedAt: now),
            CatalogFixtures.Mod("TieB", "Beta", lastVersion: now.AddDays(-1)),
            CatalogFixtures.Mod("TieA", "Alpha", lastVersion: now.AddDays(-1)),
        ], now);
        Assert.Equal(["Newest", "TieA", "TieB"], store.GetRecentlyUpdated(3).Select(m => m.Reference));
        Assert.Equal(["Newest", "TieA", "TieB", "Edited"], store.GetRecentlyUpdated(20).Select(m => m.Reference));
    }
}
