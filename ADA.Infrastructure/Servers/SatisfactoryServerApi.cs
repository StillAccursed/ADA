using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ADA.Infrastructure.Servers;

/// <summary>
/// The dedicated server's HTTPS API, used only to ask whether a server is running.
/// </summary>
/// <param name="http">
/// A client that accepts the server's certificate without checking it: servers use a self-signed
/// one by default. That's acceptable only because nothing secret is sent and the answer only means
/// "it's up". Never send the admin password or a token through this client.
/// </param>
public sealed class SatisfactoryServerApi(HttpClient http)
{
    /// <summary>True if the server answered its health check; false if nothing answered in time.</summary>
    public async Task<bool> IsRunningAsync(string address, int port, CancellationToken ct = default)
    {
        var uri = new UriBuilder(Uri.UriSchemeHttps, address, port, "/api/v1").Uri;
        try
        {
            using var response = await http.PostAsJsonAsync(uri, new ApiRequest("HealthCheck", new HealthCheckData("")),
                ServerApiJsonContext.Default.ApiRequest, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    internal sealed record HealthCheckData([property: JsonPropertyName("clientCustomData")] string ClientCustomData);

    internal sealed record ApiRequest(
        [property: JsonPropertyName("function")] string Function,
        [property: JsonPropertyName("data")] HealthCheckData Data);
}

[JsonSerializable(typeof(SatisfactoryServerApi.ApiRequest))]
internal sealed partial class ServerApiJsonContext : JsonSerializerContext;
