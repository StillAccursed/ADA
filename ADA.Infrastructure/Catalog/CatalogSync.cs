using ADA.Core.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Catalog;

public sealed record SyncResult(bool WasFullSync, int ModsUpdated, int ModsRemoved);

/// <summary>
/// Keeps <see cref="ModCatalogStore"/> in step with ficsit.app while staying gentle on the API: a
/// full pass at most once a day (picks up description edits and drops deleted mods), otherwise only
/// the mods that released a version since the last sync.
/// </summary>
public sealed class CatalogSync(IModCatalogSource source, ModCatalogStore store, TimeProvider? clock = null,
    ILogger<CatalogSync>? log = null) : IModVersionSource
{
    internal const int PAGE_SIZE = 100;

    internal const string LAST_FULL_SYNC_KEY = "last_full_sync";
    internal const string WATERMARK_KEY = "latest_version_watermark";
    internal const string LAST_CHECKED_KEY = "last_checked";

    private static readonly TimeSpan FULL_SYNC_INTERVAL = TimeSpan.FromDays(1);

    // Re-read a little before the watermark in case two releases share a timestamp.
    private static readonly TimeSpan OVERLAP = TimeSpan.FromMinutes(10);

    // Cached version lists older than this are fetched again even if the mod looks unchanged.
    private static readonly TimeSpan VERSION_CACHE_LIFETIME = TimeSpan.FromHours(6);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ILogger _log = log ?? NullLogger<CatalogSync>.Instance;

    /// <summary>
    /// Brings the local catalogue up to date: a full pass when none is recorded or the last is more
    /// than a day old, otherwise an incremental pass from the watermark. <see cref="LastChecked"/>
    /// only moves forward when the sync succeeds; failures propagate.
    /// </summary>
    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var lastFull = store.GetState(LAST_FULL_SYNC_KEY);
        var watermark = store.GetState(WATERMARK_KEY);

        var result = lastFull is null || watermark is null || now - lastFull > FULL_SYNC_INTERVAL
            ? await FullSyncAsync(now, ct)
            : await IncrementalSyncAsync(now, watermark.Value, ct);

        // Only recorded on success, so a failed check never claims the catalogue is current.
        store.SetState(LAST_CHECKED_KEY, now);
        _log.LogInformation("Catalogue {Kind} sync: {Updated} mods updated, {Removed} removed",
            result.WasFullSync ? "full" : "incremental", result.ModsUpdated, result.ModsRemoved);
        return result;
    }

    /// <summary>When the catalogue last finished a successful check against ficsit.app.</summary>
    public DateTimeOffset? LastChecked => store.GetState(LAST_CHECKED_KEY);

    public int ModCount => store.Count();

    /// <summary>Versions for a mod, from cache when fresh, otherwise from the API.</summary>
    /// <param name="refresh">Skip the cache, e.g. for changelogs missing from lists cached before they were fetched.</param>
    public async Task<IReadOnlyList<CatalogVersion>> GetVersionsAsync(string modReference, bool refresh = false,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        if (!refresh && store.GetVersions(modReference, now - VERSION_CACHE_LIFETIME) is { } cached)
        {
            return cached;
        }

        var versions = await source.GetVersionsAsync(modReference, ct);
        store.SaveVersions(modReference, versions, now);
        return versions;
    }

    private async Task<SyncResult> FullSyncAsync(DateTimeOffset startedAt, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        DateTimeOffset? newestRelease = null;

        for (var offset = 0; ; offset += PAGE_SIZE)
        {
            var page = await source.GetModsAsync(offset, PAGE_SIZE, CatalogOrder.CreatedAscending, ct);
            total = page.TotalCount;
            if (page.Mods.Count == 0)
            {
                break;
            }

            store.Upsert(page.Mods, startedAt);
            foreach (var mod in page.Mods)
            {
                seen.Add(mod.Id);
                if (mod.LastVersionDate is { } released && (newestRelease is null || released > newestRelease))
                {
                    newestRelease = released;
                }
            }

            if (offset + page.Mods.Count >= page.TotalCount)
            {
                break;
            }
        }

        // Prune only after a pass that provably saw every mod. If the listing shifted while paging,
        // keep everything rather than delete a mod we merely missed; the next pass will catch up.
        var removed = seen.Count >= total ? store.DeleteNotSeenSince(startedAt) : 0;
        store.SetState(LAST_FULL_SYNC_KEY, startedAt);
        store.SetState(WATERMARK_KEY, newestRelease ?? startedAt);
        return new SyncResult(true, seen.Count, removed);
    }

    private async Task<SyncResult> IncrementalSyncAsync(DateTimeOffset now, DateTimeOffset watermark, CancellationToken ct)
    {
        var since = watermark - OVERLAP;
        var updated = 0;
        DateTimeOffset? newestRelease = null;
        for (var offset = 0; ; offset += PAGE_SIZE)
        {
            var page = await source.GetModsAsync(offset, PAGE_SIZE, CatalogOrder.LatestVersionDescending, ct);
            var released = page.Mods.Where(m => m.LastVersionDate > since).ToList();
            if (released.Count > 0)
            {
                store.Upsert(released, now);
                // Count only genuinely new releases, not the overlap window's re-reads.
                updated += released.Count(m => m.LastVersionDate > watermark);
                newestRelease ??= released.Max(m => m.LastVersionDate);
            }

            // Pages are newest release first: once a page reaches older releases, the rest are too.
            if (released.Count < page.Mods.Count || offset + page.Mods.Count >= page.TotalCount)
            {
                break;
            }
        }

        if (newestRelease is { } n)
        {
            store.SetState(WATERMARK_KEY, n);
        }

        return new SyncResult(false, updated, 0);
    }
}
