using Polly;

namespace ADA.Infrastructure.Net;

/// <summary>The backoff every ficsit.app request uses: 1 s, 2 s, 4 s…, or longer if the server asks.</summary>
internal static class Retry
{
    /// <param name="attempt">Zero for the first retry, as Polly numbers them.</param>
    /// <param name="retryAfter">The server's <c>Retry-After</c>, used when it is longer than the backoff.</param>
    public static TimeSpan Backoff(int attempt, TimeSpan? retryAfter = null, TimeSpan? max = null)
    {
        var exponential = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var wait = retryAfter is { } ra && ra > exponential ? ra : exponential;
        return max is { } m && wait > m ? m : wait;
    }

    /// <summary>
    /// Network failures and <see cref="HttpClient"/> timeouts (a cancellation the caller didn't ask
    /// for) are worth another try.
    /// </summary>
    public static bool IsTransientException(Exception? e, ResilienceContext context) =>
        e is HttpRequestException || (e is TaskCanceledException && !context.CancellationToken.IsCancellationRequested);
}
