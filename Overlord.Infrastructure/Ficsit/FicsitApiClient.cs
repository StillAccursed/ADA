using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Overlord.Core.Catalog;

namespace Overlord.Infrastructure.Ficsit;

public sealed class FicsitApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the ficsit.app GraphQL API (<c>https://api.ficsit.app/v2/query</c>). The API's terms
/// require the calling application to be open source; Overlord is MIT-licensed.
/// </summary>
public sealed partial class FicsitApiClient : IModCatalogSource
{
    public static readonly Uri DefaultBaseAddress = new("https://api.ficsit.app/");

    // The API caps list sizes; 100 keeps a full catalogue sync to a few dozen requests.
    private const int VersionPageSize = 100;
    private const int MaxAttempts = 4;

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="http">Its <c>BaseAddress</c> is set to <see cref="DefaultBaseAddress"/> if missing.</param>
    /// <param name="delay">Backoff wait; replaced in tests so retries don't sleep.</param>
    public FicsitApiClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _http.BaseAddress ??= DefaultBaseAddress;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Overlord/0.1 (+https://github.com/StillAccursed/Overlord)");
        _delay = delay ?? Task.Delay;
    }

    public async Task<CatalogPage> GetModsAsync(int offset, int limit, CatalogOrder order, CancellationToken ct = default)
    {
        // Not updated_at: ficsit.app rewrites it on every mod routinely, so it is useless for
        // change detection and, with thousands of identical values, unstable for paging.
        var sort = order switch
        {
            CatalogOrder.CreatedAscending => "order_by: created_at, order: asc",
            _ => "order_by: last_version_date, order: desc",
        };
        var query = $$"""
            { getMods(filter: { offset: {{offset}}, limit: {{limit}}, {{sort}}, hidden: false }) {
                count
                mods { id mod_reference name short_description full_description logo downloads popularity hotness
                       updated_at last_version_date tags { name } authors { user { username } }
                       compatibility { EA { state } EXP { state } } }
            } }
            """;
        var data = await QueryAsync(query, FicsitJsonContext.Default.GraphQlResponseModsData, ct);
        return new CatalogPage(data.GetMods.Mods.Select(Map).ToList(), data.GetMods.Count);
    }

    public async Task<IReadOnlyList<CatalogVersion>> GetVersionsAsync(string modReference, CancellationToken ct = default)
    {
        // Inlined rather than sent as a variable, so it must be a plain identifier.
        if (!ModReferencePattern().IsMatch(modReference))
            throw new ArgumentException($"'{modReference}' is not a valid mod reference.", nameof(modReference));

        var all = new List<CatalogVersion>();
        for (var offset = 0; ; offset += VersionPageSize)
        {
            var query = $$"""
                { getModByReference(modReference: "{{modReference}}") {
                    versions(filter: { offset: {{offset}}, limit: {{VersionPageSize}}, order_by: created_at, order: desc }) {
                      id version game_version stability required_on_remote created_at
                      targets { targetName link size hash }
                      dependencies { mod_reference condition optional }
                    }
                } }
                """;
            var data = await QueryAsync(query, FicsitJsonContext.Default.GraphQlResponseModVersionsData, ct);
            var page = data.Mod?.Versions ?? [];
            all.AddRange(page.Select(v => Map(modReference, v)));
            if (page.Count < VersionPageSize)
                return all;
        }
    }

    private async Task<T> QueryAsync<T>(string query, JsonTypeInfo<GraphQlResponse<T>> responseType, CancellationToken ct)
        where T : class
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await _http.PostAsJsonAsync("v2/query", new GraphQlRequest(query), FicsitJsonContext.Default.GraphQlRequest, ct);
                if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                {
                    await _delay(BackoffFor(response, attempt), ct);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new FicsitApiException($"ficsit.app returned HTTP {(int)response.StatusCode}.");

                var body = await response.Content.ReadFromJsonAsync(responseType, ct);
                if (body?.Data is { } data)
                    return data;

                var message = body?.Errors is { Count: > 0 } errors
                    ? string.Join("; ", errors.Select(e => e.Message))
                    : "empty response";
                throw new FicsitApiException($"ficsit.app query failed: {message}");
            }
            // Network failures and HttpClient timeouts (TaskCanceledException without our token
            // being cancelled) are worth retrying.
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt >= MaxAttempts)
                    throw new FicsitApiException("Could not reach ficsit.app.", e);
                await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
            }
            catch (System.Text.Json.JsonException e)
            {
                throw new FicsitApiException("ficsit.app returned a response Overlord could not read.", e);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static TimeSpan BackoffFor(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        var exponential = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        return retryAfter is { } ra && ra > exponential ? ra : exponential;
    }

    private static CatalogMod Map(ModDto m) => new(
        m.Id,
        m.ModReference,
        m.Name,
        m.ShortDescription ?? "",
        m.FullDescription,
        string.IsNullOrWhiteSpace(m.Logo) ? null : m.Logo,
        m.Authors?.Select(a => a.User?.Username).OfType<string>().ToList() ?? [],
        m.Tags?.Select(t => t.Name).ToList() ?? [],
        m.Downloads,
        m.Popularity,
        m.Hotness,
        m.UpdatedAt,
        m.LastVersionDate,
        ParseCompatibility(m.Compatibility?.EA?.State),
        ParseCompatibility(m.Compatibility?.EXP?.State));

    private static CatalogVersion Map(string modReference, VersionDto v) => new(
        v.Id,
        modReference,
        v.Version,
        v.GameVersion,
        v.Stability.ToLowerInvariant() switch
        {
            "release" => VersionStability.Release,
            "beta" => VersionStability.Beta,
            _ => VersionStability.Alpha,
        },
        v.RequiredOnRemote,
        v.CreatedAt,
        v.Targets?
            .Select(t => Enum.TryParse<TargetPlatform>(t.TargetName, ignoreCase: true, out var p)
                ? new VersionTarget(p, t.Link, t.Size, t.Hash)
                : null)
            .OfType<VersionTarget>()
            .ToList() ?? [],
        v.Dependencies?.Select(d => new VersionDependency(d.ModReference, d.Condition, d.Optional)).ToList() ?? []);

    private static CompatibilityState ParseCompatibility(string? state) =>
        Enum.TryParse<CompatibilityState>(state, ignoreCase: true, out var s) ? s : CompatibilityState.Unknown;

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex ModReferencePattern();
}
