using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using ADA.Core.Catalog;
using ADA.Infrastructure.Net;
using Polly;
using Polly.Retry;

namespace ADA.Infrastructure.Ficsit;

public sealed class FicsitApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the ficsit.app GraphQL API (<c>https://api.ficsit.app/v2/query</c>). The API's terms
/// require the calling application to be open source; ADA is MIT-licensed.
/// </summary>
public sealed class FicsitApiClient : IModCatalogSource
{
    // The API caps list sizes; 100 keeps a full catalogue sync to a few dozen requests.
    private const int VersionPageSize = 100;
    private const int MaxAttempts = 4;

    private static readonly Uri QueryEndpoint = new(FicsitApi.BaseAddress, "v2/query");

    // Sort orders are fixed per CatalogOrder, so they are part of the query text; everything that
    // varies is sent as a GraphQL variable. Not updated_at: ficsit.app rewrites it on every mod
    // routinely, so it is useless for change detection and, with thousands of identical values,
    // unstable for paging.
    private const string LatestFields = "version game_version targets { targetName }";

    private const string ModFields = $$"""
        count
        mods { id mod_reference name short_description full_description logo downloads popularity hotness
               updated_at last_version_date tags { name } authors { user { username } }
               compatibility { EA { state } EXP { state } } views
               latestVersions { release { {{LatestFields}} } beta { {{LatestFields}} } alpha { {{LatestFields}} } } }
        """;

    private const string ModsByCreated = $$"""
        query Mods($offset: Int!, $limit: Int!) {
          getMods(filter: { offset: $offset, limit: $limit, order_by: created_at, order: asc, hidden: false }) { {{ModFields}} }
        }
        """;

    private const string ModsByLatestVersion = $$"""
        query Mods($offset: Int!, $limit: Int!) {
          getMods(filter: { offset: $offset, limit: $limit, order_by: last_version_date, order: desc, hidden: false }) { {{ModFields}} }
        }
        """;

    private const string Versions = """
        query Versions($modReference: ModReference!, $offset: Int!, $limit: Int!) {
          getModByReference(modReference: $modReference) {
            versions(filter: { offset: $offset, limit: $limit, order_by: created_at, order: desc }) {
              id version game_version stability required_on_remote created_at changelog
              targets { targetName link size hash }
              dependencies { mod_reference condition optional }
            }
          }
        }
        """;

    private readonly HttpClient _http;
    private readonly ResiliencePipeline<HttpResponseMessage> _retry;

    /// <param name="http">Configured by the app (timeout, User-Agent); requests use absolute URLs.</param>
    /// <param name="time">Drives retry waits; tests pass one that doesn't sleep.</param>
    public FicsitApiClient(HttpClient http, TimeProvider? time = null)
    {
        _http = http;
        _retry = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = time ?? TimeProvider.System }
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = MaxAttempts - 1,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Result is { } r ? IsTransient(r.StatusCode) : Retry.IsTransientException(args.Outcome.Exception, args.Context)),
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(
                    Retry.Backoff(args.AttemptNumber, args.Outcome.Result?.Headers.RetryAfter?.Delta)),
            })
            .Build();
    }

    public async Task<CatalogPage> GetModsAsync(int offset, int limit, CatalogOrder order, CancellationToken ct = default)
    {
        var query = order == CatalogOrder.CreatedAscending ? ModsByCreated : ModsByLatestVersion;
        var data = await QueryAsync(new GraphQlRequest<PageVariables>(query, new PageVariables(offset, limit)),
            FicsitJsonContext.Default.GraphQlRequestPageVariables, FicsitJsonContext.Default.GraphQlResponseModsData, ct);
        return new CatalogPage(data.GetMods.Mods.Select(Map).ToList(), data.GetMods.Count);
    }

    public async Task<IReadOnlyList<CatalogVersion>> GetVersionsAsync(string modReference, CancellationToken ct = default)
    {
        if (!ModIdentifiers.IsValidReference(modReference))
            throw new ArgumentException($"'{modReference}' is not a valid mod reference.", nameof(modReference));

        var all = new List<CatalogVersion>();
        for (var offset = 0; ; offset += VersionPageSize)
        {
            var request = new GraphQlRequest<VersionVariables>(Versions, new VersionVariables(modReference, offset, VersionPageSize));
            var data = await QueryAsync(request, FicsitJsonContext.Default.GraphQlRequestVersionVariables,
                FicsitJsonContext.Default.GraphQlResponseModVersionsData, ct);
            var page = data.Mod?.Versions ?? [];
            all.AddRange(page.Select(v => Map(modReference, v)));
            if (page.Count < VersionPageSize)
                return all;
        }
    }

    private async Task<T> QueryAsync<TVariables, T>(GraphQlRequest<TVariables> request,
        JsonTypeInfo<GraphQlRequest<TVariables>> requestType, JsonTypeInfo<GraphQlResponse<T>> responseType, CancellationToken ct)
        where T : class
    {
        try
        {
            using var response = await _retry.ExecuteAsync(
                async token => await _http.PostAsJsonAsync(QueryEndpoint, request, requestType, token), ct);

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
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new FicsitApiException("Could not reach ficsit.app.", e);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new FicsitApiException("ficsit.app returned a response ADA could not read.", e);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static CatalogMod Map(ModDto m) => new(
        m.Id,
        m.ModReference,
        m.Name,
        m.ShortDescription ?? "",
        m.FullDescription,
        string.IsNullOrWhiteSpace(m.Logo) ? null : m.Logo,
        m.Authors?.Select(a => a.User?.Username).OfType<string>().ToList() ?? [],
        // Some tags arrive padded ("multiplayer ").
        m.Tags?.Select(t => t.Name.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [],
        m.Downloads,
        m.Popularity,
        m.Hotness,
        m.UpdatedAt,
        m.LastVersionDate,
        ParseCompatibility(m.Compatibility?.EA?.State),
        ParseCompatibility(m.Compatibility?.EXP?.State),
        m.Views,
        new[] { (VersionStability.Release, m.LatestVersions?.Release), (VersionStability.Beta, m.LatestVersions?.Beta),
                (VersionStability.Alpha, m.LatestVersions?.Alpha) }
            .Where(l => l.Item2 is not null)
            .Select(l => new LatestVersion(l.Item1, l.Item2!.Version, l.Item2.GameVersion,
                l.Item2.Targets?.Select(t => Enum.TryParse<TargetPlatform>(t.TargetName, ignoreCase: true, out var p) ? (TargetPlatform?)p : null)
                    .OfType<TargetPlatform>().ToList() ?? []))
            .ToList());

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
        v.Dependencies?.Select(d => new VersionDependency(d.ModReference, d.Condition, d.Optional)).ToList() ?? [],
        v.Changelog);

    private static CompatibilityState ParseCompatibility(string? state) =>
        Enum.TryParse<CompatibilityState>(state, ignoreCase: true, out var s) ? s : CompatibilityState.Unknown;
}
