using ADA.Core.Catalog;
using ADA.Infrastructure.Catalog;
using ADA.Tests.Fixtures;
using static ADA.Tests.Fixtures.CatalogFixtures;

namespace ADA.Tests.Catalog;

public sealed class CatalogSyncTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly ModCatalogStore _store;
    private readonly FakeSource _source = new();
    private readonly ManualClock _clock = new(T0);
    private readonly CatalogSync _sync;

    public CatalogSyncTests()
    {
        _store = new ModCatalogStore(_dir.Combine("catalog.db"));
        _sync = new CatalogSync(_source, _store, _clock);
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>In-memory ficsit.app that honours the requested order, like the real API.</summary>
    private sealed class FakeSource : IModCatalogSource
    {
        public List<CatalogMod> Mods { get; } = [];
        public List<CatalogOrder> PageRequests { get; } = [];
        public int VersionRequests { get; private set; }

        /// <summary>Simulates the listing shifting under us: hide this many mods from the next pass.</summary>
        public int HideFromListing { get; set; }

        public Task<CatalogPage> GetModsAsync(int offset, int limit, CatalogOrder order, CancellationToken ct = default)
        {
            PageRequests.Add(order);
            IEnumerable<CatalogMod> sorted = order == CatalogOrder.CreatedAscending
                ? Mods // insertion order stands in for created_at
                : Mods.OrderByDescending(m => m.LastVersionDate ?? DateTimeOffset.MinValue);
            var page = sorted.Skip(HideFromListing).Skip(offset).Take(limit).ToList();
            return Task.FromResult(new CatalogPage(page, Mods.Count));
        }

        public Task<IReadOnlyList<CatalogVersion>> GetVersionsAsync(string modReference, CancellationToken ct = default)
        {
            VersionRequests++;
            return Task.FromResult<IReadOnlyList<CatalogVersion>>([Version(modReference, "1.0.0")]);
        }
    }

    private void AddMods(int count, DateTimeOffset releasedAt)
    {
        for (var i = 0; i < count; i++)
        {
            _source.Mods.Add(Mod($"Mod{_source.Mods.Count}", lastVersion: releasedAt.AddMinutes(-i)));
        }
    }

    [Fact]
    public async Task First_sync_pages_through_everything_in_creation_order()
    {
        AddMods(250, T0.AddDays(-30));

        var result = await _sync.SyncAsync();

        Assert.True(result.WasFullSync);
        Assert.Equal(250, result.ModsUpdated);
        Assert.Equal(250, _store.Count());
        Assert.Equal([CatalogOrder.CreatedAscending, CatalogOrder.CreatedAscending, CatalogOrder.CreatedAscending], _source.PageRequests);
    }

    [Fact]
    public async Task Later_syncs_only_fetch_new_releases()
    {
        AddMods(250, T0.AddDays(-30));
        await _sync.SyncAsync();
        _source.PageRequests.Clear();

        _clock.Now = T0.AddHours(1);
        _source.Mods[200] = _source.Mods[200] with { Name = "Freshly Released", LastVersionDate = T0.AddMinutes(30) };

        var result = await _sync.SyncAsync();

        Assert.False(result.WasFullSync);
        Assert.Equal(1, result.ModsUpdated);
        Assert.Equal([CatalogOrder.LatestVersionDescending], _source.PageRequests);
        Assert.Equal("Freshly Released", _store.Get(_source.Mods[200].Reference)?.Name);
    }

    [Fact]
    public async Task Daily_full_sync_removes_deleted_mods()
    {
        AddMods(3, T0.AddDays(-30));
        await _sync.SyncAsync();

        var deleted = _source.Mods[1].Reference;
        _source.Mods.RemoveAt(1);
        _clock.Now = T0.AddDays(2);

        var result = await _sync.SyncAsync();

        Assert.True(result.WasFullSync);
        Assert.Equal(1, result.ModsRemoved);
        Assert.Null(_store.Get(deleted));
    }

    [Fact]
    public async Task Full_sync_that_missed_mods_does_not_prune()
    {
        AddMods(5, T0.AddDays(-30));
        await _sync.SyncAsync();

        _clock.Now = T0.AddDays(2);
        _source.HideFromListing = 2; // the API's listing shifted; two mods never came back

        var result = await _sync.SyncAsync();

        Assert.Equal(0, result.ModsRemoved);
        Assert.Equal(5, _store.Count());
    }

    [Fact]
    public async Task Failed_full_sync_does_not_prune()
    {
        AddMods(3, T0.AddDays(-30));
        await _sync.SyncAsync();
        _clock.Now = T0.AddDays(2);

        var failing = new CatalogSync(new ThrowingSource(), _store, _clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.SyncAsync());

        Assert.Equal(3, _store.Count());
    }

    [Fact]
    public async Task Records_last_checked_only_on_success()
    {
        Assert.Null(_sync.LastChecked);
        AddMods(2, T0.AddDays(-1));
        await _sync.SyncAsync();
        Assert.Equal(T0, _sync.LastChecked);
        Assert.Equal(2, _sync.ModCount);

        _clock.Now = T0.AddHours(3);
        var failing = new CatalogSync(new ThrowingSource(), _store, _clock);
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.SyncAsync());
        Assert.Equal(T0, _sync.LastChecked);
    }

    private sealed class ThrowingSource : IModCatalogSource
    {
        public Task<CatalogPage> GetModsAsync(int offset, int limit, CatalogOrder order, CancellationToken ct = default) =>
            throw new HttpRequestException("offline");

        public Task<IReadOnlyList<CatalogVersion>> GetVersionsAsync(string modReference, CancellationToken ct = default) =>
            throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task Versions_are_cached_until_stale()
    {
        await _sync.GetVersionsAsync("M");
        await _sync.GetVersionsAsync("M");
        Assert.Equal(1, _source.VersionRequests);

        _clock.Now = T0.AddHours(7);
        await _sync.GetVersionsAsync("M");
        Assert.Equal(2, _source.VersionRequests);
    }

    [Fact]
    public async Task Version_cache_survives_resyncs_until_a_new_release()
    {
        AddMods(3, T0.AddDays(-1));
        await _sync.SyncAsync();
        var mod = _source.Mods[1];
        await _sync.GetVersionsAsync(mod.Reference);

        // A full resync rewrites every mod (ficsit.app also bumps updated_at constantly)...
        _clock.Now = T0.AddHours(2);
        _source.Mods[1] = mod with { UpdatedAt = T0.AddHours(2), Downloads = 999 };
        _store.SetState(CatalogSync.LAST_FULL_SYNC_KEY, T0.AddDays(-5));
        await _sync.SyncAsync();
        await _sync.GetVersionsAsync(mod.Reference);
        Assert.Equal(1, _source.VersionRequests);

        // ...but only a new release clears the cached versions.
        _source.Mods[1] = mod with { LastVersionDate = T0.AddHours(2) };
        await _sync.SyncAsync();
        await _sync.GetVersionsAsync(mod.Reference);
        Assert.Equal(2, _source.VersionRequests);
    }
}
