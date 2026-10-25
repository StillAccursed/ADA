namespace Overlord.Infrastructure.Threading;

internal static class Concurrently
{
    /// <summary>
    /// Runs <paramref name="map"/> on every item, at most <paramref name="limit"/> at a time, and
    /// returns the results in the items' order. The first failure cancels the rest and is rethrown.
    /// </summary>
    public static async Task<TResult[]> MapAsync<T, TResult>(IReadOnlyList<T> items, int limit,
        Func<T, CancellationToken, ValueTask<TResult>> map, CancellationToken ct)
    {
        var results = new TResult[items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = limit, CancellationToken = ct },
            async (i, token) => results[i] = await map(items[i], token));
        return results;
    }
}
