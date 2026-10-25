using Overlord.Core.Catalog;
using Overlord.Core.Resolution;

namespace Overlord.Tests.Resolution;

public sealed class DependencyResolverTests
{
    private const int Game = 513885;

    private readonly Dictionary<string, List<CatalogVersion>> _catalog = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _fetched = [];

    /// <summary>Publishes a version. Dependencies are "Ref range" or "Ref range?" for optional.</summary>
    private void Publish(string reference, string version, string game = ">=513885",
        VersionStability stability = VersionStability.Release, TargetPlatform[]? platforms = null, params string[] deps)
    {
        var targets = (platforms ?? [TargetPlatform.Windows])
            .Select(p => new VersionTarget(p, $"/v1/{reference}/{version}/{p}", 1, "h")).ToList();
        var dependencies = deps.Select(d =>
        {
            var optional = d.EndsWith('?');
            var parts = d.TrimEnd('?').Split(' ', 2);
            return new VersionDependency(parts[0], parts[1], optional);
        }).ToList();
        if (!_catalog.TryGetValue(reference, out var list))
            _catalog[reference] = list = [];
        list.Add(new CatalogVersion($"{reference}-{version}", reference, version, game, stability, false,
            DateTimeOffset.UnixEpoch, targets, dependencies));
    }

    private Task<ResolveResult> Resolve(params string[] requested) => Resolve(null, requested);

    private Task<ResolveResult> Resolve(IReadOnlyDictionary<string, string>? preferred, params string[] requested)
    {
        var resolver = new DependencyResolver((reference, _) =>
        {
            lock (_fetched)
                _fetched.Add(reference);
            return Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(reference) ?? []);
        });
        var requirements = requested.Select(r =>
        {
            var parts = r.Split(' ', 2);
            return new ModRequirement(parts[0], parts.Length > 1 ? parts[1] : null);
        }).ToList();
        return resolver.ResolveAsync(new ResolveRequest(requirements, Game, TargetPlatform.Windows, preferred));
    }

    private static string[] Picked(ResolveResult r) => r.Mods.Select(m => $"{m.Reference} {m.Version.Version}").ToArray();

    [Fact]
    public async Task Picks_the_newest_compatible_versions_with_dependencies_first()
    {
        Publish("SML", "3.11.0");
        Publish("SML", "3.12.0");
        Publish("WeatherExtended", "1.0.0", deps: "SML ^3.12.0");
        Publish("WeatherExtended", "1.1.0", deps: "SML ^3.12.0");

        var result = await Resolve("WeatherExtended");

        Assert.True(result.Succeeded, string.Join(" ", result.Problems));
        Assert.Equal(["SML 3.12.0", "WeatherExtended 1.1.0"], Picked(result));
        Assert.Equal(["WeatherExtended 1.1.0"], result.Mods[0].RequiredBy);
        Assert.Equal(["requested"], result.Mods[1].RequiredBy);
    }

    [Fact]
    public async Task Skips_versions_for_other_game_builds_or_platforms()
    {
        Publish("SML", "3.12.0");
        Publish("SML", "3.13.0", game: ">=520000");
        Publish("ServerTool", "2.0.0", platforms: [TargetPlatform.WindowsServer]);
        Publish("ServerTool", "1.0.0");

        var result = await Resolve("SML", "ServerTool");

        // Unrelated mods are listed by name, ignoring case.
        Assert.Equal(["ServerTool 1.0.0", "SML 3.12.0"], Picked(result));
    }

    [Fact]
    public async Task Prefers_release_over_newer_betas()
    {
        Publish("A", "1.0.0");
        Publish("A", "1.1.0", stability: VersionStability.Beta);

        Assert.Equal(["A 1.0.0"], Picked(await Resolve("A")));
    }

    [Fact]
    public async Task Pre_release_versions_can_be_chosen_when_nothing_else_fits_or_they_are_kept()
    {
        Publish("SML", "3.12.0");
        Publish("SML", "3.13.0-beta.1", stability: VersionStability.Beta);
        Publish("A", "1.0.0", deps: "SML ^3.13.0-beta.1");

        Assert.Equal(["SML 3.13.0-beta.1", "A 1.0.0"], Picked(await Resolve("A")));
        Assert.Equal(["SML 3.13.0-beta.1"], Picked(await Resolve(new Dictionary<string, string> { ["SML"] = "3.13.0-beta.1" }, "SML")));
        Assert.Equal(["SML 3.12.0"], Picked(await Resolve("SML")));
    }

    [Fact]
    public async Task Keeps_a_preferred_version_while_it_still_fits()
    {
        Publish("SML", "3.12.0");
        Publish("SML", "3.12.5");
        Publish("A", "1.0.0", deps: "SML ^3.12.0");

        var result = await Resolve(new Dictionary<string, string> { ["SML"] = "3.12.0" }, "A");

        Assert.Equal(["SML 3.12.0", "A 1.0.0"], Picked(result));
    }

    [Fact]
    public async Task Ignores_a_preferred_version_that_no_longer_fits()
    {
        Publish("SML", "3.11.0");
        Publish("SML", "3.12.0");
        Publish("A", "1.0.0", deps: "SML ^3.12.0");

        var result = await Resolve(new Dictionary<string, string> { ["SML"] = "3.11.0" }, "A");

        Assert.Equal(["SML 3.12.0", "A 1.0.0"], Picked(result));
    }

    [Fact]
    public async Task Backtracks_to_an_older_version_when_the_newest_cannot_work()
    {
        Publish("SML", "3.12.0");
        Publish("SML", "3.13.0", game: ">=520000"); // not for this game build
        Publish("A", "2.0.0", deps: "SML ^3.13.0");
        Publish("A", "1.5.0", deps: "SML ^3.12.0");

        var result = await Resolve("A");

        Assert.Equal(["SML 3.12.0", "A 1.5.0"], Picked(result));
    }

    [Fact]
    public async Task Finds_versions_that_satisfy_two_mods_sharing_a_library()
    {
        Publish("Lib", "1.0.0");
        Publish("Lib", "1.5.0");
        Publish("Lib", "2.0.0");
        Publish("A", "1.0.0", deps: "Lib ^1.0.0");
        Publish("B", "1.0.0", deps: "Lib >=1.2.0");

        Assert.Equal(new[] { "A 1.0.0", "Lib 1.5.0", "B 1.0.0" }.Order(), Picked(await Resolve("A", "B")).Order());
    }

    [Fact]
    public async Task Bare_versions_mean_exactly_that_version()
    {
        // Seen on ficsit.app between mods released together (e.g. KLib and KAPI).
        Publish("KLib", "1.0.0");
        Publish("KLib", "1.0.1");
        Publish("KAPI", "1.0.0", deps: "KLib 1.0.0");

        Assert.Contains("KLib 1.0.0", Picked(await Resolve("KAPI")));
    }

    [Fact]
    public async Task Respects_a_requested_range()
    {
        Publish("A", "1.0.0");
        Publish("A", "2.0.0");

        Assert.Equal(["A 1.0.0"], Picked(await Resolve("A ^1.0.0")));
    }

    [Fact]
    public async Task Optional_dependencies_are_not_pulled_in_but_must_fit_when_present()
    {
        Publish("Extra", "1.0.0");
        Publish("Extra", "2.0.0");
        Publish("A", "1.0.0", deps: "Extra ^1.0.0?");

        Assert.Equal(["A 1.0.0"], Picked(await Resolve("A")));
        Assert.Equal(["Extra 1.0.0", "A 1.0.0"], Picked(await Resolve("A", "Extra")));
    }

    [Fact]
    public async Task Handles_dependency_cycles()
    {
        Publish("A", "1.0.0", deps: "B ^1.0.0");
        Publish("B", "1.0.0", deps: "A ^1.0.0");

        var result = await Resolve("A");

        Assert.True(result.Succeeded);
        Assert.Equal(["A 1.0.0", "B 1.0.0"], Picked(result).Order());
    }

    [Fact]
    public async Task Explains_when_the_mod_loader_does_not_support_the_game_yet()
    {
        Publish("SML", "3.13.0", game: ">=520000");
        Publish("A", "1.0.0", deps: "SML ^3.13.0");

        var result = await Resolve("A");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Contains("No version of SML supports game build 513885") && p.Contains(">=520000")
                                              && p.Contains("A 1.0.0"));
    }

    [Fact]
    public async Task Explains_a_requirement_no_version_meets()
    {
        Publish("SML", "3.12.0");
        Publish("SML", "3.13.0", game: ">=520000");
        Publish("A", "1.0.0", deps: "SML ^3.13.0");

        var result = await Resolve("A");

        Assert.Equal("A 1.0.0 needs SML ^3.13.0, but no SML that supports game build 513885 matches (the newest that does is 3.12.0).",
            Assert.Single(result.Problems));
    }

    [Fact]
    public async Task Explains_two_mods_that_need_incompatible_versions()
    {
        Publish("Lib", "1.0.0");
        Publish("Lib", "2.0.0");
        Publish("A", "1.0.0", deps: "Lib ^1.0.0");
        Publish("B", "1.0.0", deps: "Lib ^2.0.0");

        var result = await Resolve("A", "B");

        Assert.False(result.Succeeded);
        var problem = result.Problems[0];
        Assert.Contains("Lib", problem);
        Assert.Contains("A 1.0.0", problem);
        Assert.Contains("B 1.0.0", problem);
    }

    [Fact]
    public async Task Explains_missing_mods_and_platform_builds()
    {
        Publish("ServerOnly", "1.0.0", platforms: [TargetPlatform.WindowsServer]);
        Publish("A", "1.0.0", deps: "Gone ^1.0.0");

        Assert.Equal("ServerOnly has no Windows build.", Assert.Single((await Resolve("ServerOnly")).Problems));
        Assert.Equal("Gone is not on ficsit.app. It is needed by A 1.0.0.", Assert.Single((await Resolve("A")).Problems));
    }

    [Fact]
    public async Task Rejects_invalid_requests_before_fetching_anything()
    {
        var result = await Resolve("bad name", "A not-a-range");

        Assert.Equal(2, result.Problems.Count);
        Assert.Empty(_fetched);
    }

    [Fact]
    public async Task Fetches_each_mod_once()
    {
        Publish("SML", "3.12.0");
        Publish("Lib", "1.0.0", deps: "SML ^3.12.0");
        Publish("A", "1.0.0", ">=513885", VersionStability.Release, null, "Lib ^1.0.0", "SML ^3.12.0");
        Publish("B", "1.0.0", ">=513885", VersionStability.Release, null, "Lib ^1.0.0", "SML ^3.12.0");

        await Resolve("A", "B");

        Assert.Equal(["A", "B", "Lib", "SML"], _fetched.Order());
    }

    [Fact]
    public async Task Gives_up_cleanly_past_the_step_limit()
    {
        for (var i = 0; i < 6; i++)
        {
            Publish("Lib", $"1.{i}.0");
            Publish($"M{i}", "1.0.0", deps: "Lib ^1.0.0");
        }
        Publish("Last", "1.0.0", deps: "Lib ^2.0.0"); // forces every combination to fail

        var resolver = new DependencyResolver((r, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(r) ?? []))
        {
            StepLimit = 3,
        };
        var result = await resolver.ResolveAsync(new ResolveRequest(
            [.. Enumerable.Range(0, 6).Select(i => new ModRequirement($"M{i}")), new ModRequirement("Last")], Game, TargetPlatform.Windows));

        Assert.False(result.Succeeded);
    }
}
