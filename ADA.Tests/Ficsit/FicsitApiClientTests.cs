using System.Net;
using ADA.Core.Catalog;
using ADA.Infrastructure.Ficsit;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Ficsit;

public class FicsitApiClientTests
{
    // Shapes recorded from the live API on 2026-10-25 (trimmed).
    private const string ModsPage = """
        { "data": { "getMods": { "count": 2, "mods": [
          { "id": "8tof4SFdKiL6tP", "mod_reference": "WeatherExtended", "name": "Weather Extended",
            "short_description": "Darker nights plus new weather.", "full_description": "## Weather\n![Fog at night](https://x/fog.png)",
            "logo": "https://storage.ficsit.app/logo.webp", "downloads": 1200, "popularity": 50, "hotness": 7,
            "updated_at": "2026-10-20T12:00:00Z", "last_version_date": "2026-10-19T08:30:00Z",
            "tags": [ { "name": "Visual" } ], "authors": [ { "user": { "username": "59thCasper" } } ],
            "compatibility": { "EA": { "state": "Works" }, "EXP": { "state": "Damaged" } } },
          { "id": "smlid", "mod_reference": "SML", "name": "Satisfactory Mod Loader", "short_description": "Loader",
            "full_description": null, "logo": "", "downloads": 9, "popularity": 9, "hotness": 9,
            "updated_at": "2026-10-01T00:00:00Z", "last_version_date": null, "tags": [], "authors": [], "compatibility": null }
        ] } } }
        """;

    private const string SmlVersions = """
        { "data": { "getModByReference": { "versions": [
          { "id": "74YqQBq2KJKN77", "version": "3.12.0", "game_version": ">=491125", "stability": "release",
            "required_on_remote": true, "created_at": "2026-09-01T00:00:00Z",
            "targets": [
              { "targetName": "Windows", "link": "/v1/version/74YqQBq2KJKN77/Windows/download", "size": 38752019,
                "hash": "ff2f1d5a5d7af5f2fe896305cf6389cf7f2200e392975f5e11a20a67146107af" },
              { "targetName": "LinuxServer", "link": "/v1/version/74YqQBq2KJKN77/LinuxServer/download", "size": 6627612, "hash": null },
              { "targetName": "SomeFuturePlatform", "link": "/x", "size": 1, "hash": null } ],
            "dependencies": [] },
          { "id": "v2", "version": "1.1.0", "game_version": ">=513885", "stability": "beta",
            "required_on_remote": false, "created_at": "2026-08-01T00:00:00Z", "targets": null,
            "dependencies": [ { "mod_reference": "SML", "condition": "^3.12.0", "optional": false } ] }
        ] } } }
        """;

    private static (FicsitApiClient Client, FakeHttpHandler Handler, InstantTimeProvider Time) Create()
    {
        var handler = new FakeHttpHandler();
        var time = new InstantTimeProvider();
        return (new FicsitApiClient(new HttpClient(handler), time), handler, time);
    }

    [Fact]
    public async Task Maps_mods_page()
    {
        var (client, handler, _) = Create();
        handler.Respond(ModsPage);

        var page = await client.GetModsAsync(0, 100, CatalogOrder.CreatedAscending);

        Assert.Equal(2, page.TotalCount);
        var weather = page.Mods[0];
        Assert.Equal("WeatherExtended", weather.Reference);
        Assert.Equal(["59thCasper"], weather.Authors);
        Assert.Equal(["Visual"], weather.Tags);
        Assert.Equal(CompatibilityState.Works, weather.CompatibilityRelease);
        Assert.Equal(CompatibilityState.Damaged, weather.CompatibilityExperimental);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), weather.UpdatedAt);

        var sml = page.Mods[1];
        Assert.Null(sml.LogoUrl);
        Assert.Equal(CompatibilityState.Unknown, sml.CompatibilityRelease);
        var body = handler.RequestBodies.Single();
        Assert.Contains("order_by: created_at, order: asc", body);
        Assert.Contains("\"variables\":{\"offset\":0,\"limit\":100}", body);
    }

    [Fact]
    public async Task Maps_versions_targets_and_dependencies()
    {
        var (client, handler, _) = Create();
        handler.Respond(SmlVersions);

        var versions = await client.GetVersionsAsync("SML");

        var sml = versions[0];
        Assert.Equal(">=491125", sml.GameVersionRange);
        Assert.True(sml.RequiredOnRemote);
        Assert.Equal(2, sml.Targets.Count); // unknown platform names are skipped
        Assert.Equal(38752019, sml.TargetFor(TargetPlatform.Windows)?.Size);
        Assert.Null(sml.TargetFor(TargetPlatform.WindowsServer));

        var mod = versions[1];
        Assert.Equal(VersionStability.Beta, mod.Stability);
        Assert.Empty(mod.Targets);
        Assert.Equal(new VersionDependency("SML", "^3.12.0", false), Assert.Single(mod.Dependencies));
    }

    [Fact]
    public async Task Pages_through_versions_until_a_short_page()
    {
        var (client, handler, _) = Create();
        var version = """{ "id": "v", "version": "1.0.0", "game_version": ">=1", "stability": "release", "required_on_remote": false, "created_at": "2026-01-01T00:00:00Z", "targets": [], "dependencies": [] }""";
        handler.Respond($$"""{ "data": { "getModByReference": { "versions": [ {{string.Join(",", Enumerable.Repeat(version, 100))}} ] } } }""");
        handler.Respond($$"""{ "data": { "getModByReference": { "versions": [ {{version}} ] } } }""");

        var versions = await client.GetVersionsAsync("BigMod");

        Assert.Equal(101, versions.Count);
        Assert.Contains("\"offset\":100", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task Retries_rate_limits_and_server_errors_with_backoff()
    {
        var (client, handler, time) = Create();
        handler.Fail(HttpStatusCode.TooManyRequests).Fail(HttpStatusCode.BadGateway).Respond(ModsPage);

        var page = await client.GetModsAsync(0, 100, CatalogOrder.CreatedAscending);

        Assert.Equal(2, page.Mods.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], time.Delays);
    }

    [Fact]
    public async Task Gives_up_after_repeated_failures()
    {
        var (client, handler, _) = Create();
        for (var i = 0; i < 4; i++)
            handler.Fail(HttpStatusCode.ServiceUnavailable);

        var e = await Assert.ThrowsAsync<FicsitApiException>(() => client.GetModsAsync(0, 100, CatalogOrder.CreatedAscending));
        Assert.Contains("503", e.Message);
    }

    [Fact]
    public async Task GraphQL_errors_without_data_throw()
    {
        var (client, handler, _) = Create();
        handler.Respond("""{ "errors": [ { "message": "could not find SML version" } ], "data": null }""");

        var e = await Assert.ThrowsAsync<FicsitApiException>(() => client.GetModsAsync(0, 100, CatalogOrder.CreatedAscending));
        Assert.Contains("could not find SML version", e.Message);
    }

    [Fact]
    public async Task Unknown_mod_has_no_versions()
    {
        var (client, handler, _) = Create();
        handler.Respond("""{ "data": { "getModByReference": null } }""");

        Assert.Empty(await client.GetVersionsAsync("NoSuchMod"));
    }

    [Theory]
    [InlineData("Weather\") { evil }")]
    [InlineData("")]
    [InlineData("has space")]
    public async Task Rejects_references_that_are_not_identifiers(string reference)
    {
        var (client, _, _) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetVersionsAsync(reference));
    }
}
