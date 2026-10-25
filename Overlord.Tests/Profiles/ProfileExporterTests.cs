using System.Text;
using System.Text.Json;
using Overlord.Core.Catalog;
using Overlord.Core.Profiles;
using Overlord.Infrastructure.Profiles;
using Overlord.Tests.Fixtures;

namespace Overlord.Tests.Profiles;

public sealed class ProfileExporterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Dictionary<string, List<CatalogVersion>> _catalog = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ModProfile Atria = new("Atria",
    [
        new ProfileMod("WeatherExtended", "1.1.0"),
        new ProfileMod("SML", "3.12.0"),
    ]);

    public ProfileExporterTests()
    {
        Publish("SML", "3.12.0", TargetPlatform.Windows, TargetPlatform.WindowsServer, TargetPlatform.LinuxServer);
        Publish("WeatherExtended", "1.1.0", TargetPlatform.Windows);
    }

    public void Dispose() => _dir.Dispose();

    private ProfileExporter Exporter() =>
        new((reference, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>(_catalog.GetValueOrDefault(reference) ?? []));

    private void Publish(string reference, string version, params TargetPlatform[] platforms)
    {
        var targets = platforms
            .Select(p => new VersionTarget(p, $"/v1/version/{reference}{version}/{p}/download", 100, $"hash-{reference}-{p}"))
            .ToList();
        if (!_catalog.TryGetValue(reference, out var list))
            _catalog[reference] = list = [];
        list.Add(new CatalogVersion($"{reference}{version}", reference, version, ">=513885", VersionStability.Release, false,
            DateTimeOffset.UnixEpoch, targets, []));
    }

    private Task<IReadOnlyList<string>> Export(ProfileFormats formats, ModProfile? profile = null) =>
        Exporter().ExportAsync(profile ?? Atria, _dir.Combine("Atria"), formats, 513885, TargetPlatform.Windows);

    [Fact]
    public async Task Exports_olp_only()
    {
        var written = await Export(ProfileFormats.Olp);

        Assert.Equal([_dir.Combine("Atria.olp")], written);
        Assert.Equal(["SML", "WeatherExtended"], OlpFile.Read(written[0]).Mods.Select(m => m.Name));
        Assert.False(File.Exists(_dir.Combine("Atria.smmprofile")));
    }

    [Fact]
    public async Task Exports_both_formats_side_by_side()
    {
        var written = await Export(ProfileFormats.Both);

        Assert.Equal([_dir.Combine("Atria.olp"), _dir.Combine("Atria.smmprofile")], written);
    }

    [Fact]
    public async Task Smmprofile_is_complete_on_its_own_for_Satisfactory_Mod_Manager()
    {
        var written = await Export(ProfileFormats.SmmProfile);
        var doc = SmmProfileFile.Read(written.Single());

        Assert.Equal("Atria", doc.Profile!.Name);
        Assert.Equal(["Windows"], doc.Profile.RequiredTargets);
        Assert.Equal(["SML", "WeatherExtended"], doc.Profile.Mods!.Keys);
        Assert.All(doc.Profile.Mods.Values, m => Assert.Equal(">=0.0.0", m.Version));
        Assert.Contains("\"version\": \">=0.0.0\"", File.ReadAllText(written.Single()));

        Assert.Equal(1, doc.Lockfile!.Version);
        var sml = doc.Lockfile.Mods!["SML"];
        Assert.Equal("3.12.0", sml.Version);
        Assert.Equal(["LinuxServer", "Windows", "WindowsServer"], sml.Targets!.Keys);
        Assert.Equal("https://api.ficsit.app/v1/version/SML3.12.0/Windows/download", sml.Targets["Windows"].Link);
        Assert.Equal("hash-SML-Windows", sml.Targets["Windows"].Hash);
        Assert.Equal(["Windows"], doc.Lockfile.Mods["WeatherExtended"].Targets!.Keys);

        Assert.Equal(513885, doc.Metadata!.GameVersion);
    }

    [Fact]
    public async Task Smmprofile_uses_the_same_field_names_and_nesting_as_the_sample()
    {
        var written = await Export(ProfileFormats.SmmProfile);
        using var json = JsonDocument.Parse(File.ReadAllText(written.Single()));
        var root = json.RootElement;

        Assert.Equal(["profile", "lockfile", "metadata"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["mods", "name", "required_targets"], root.GetProperty("profile").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["version", "enabled"], root.GetProperty("profile").GetProperty("mods").GetProperty("SML").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["mods", "version"], root.GetProperty("lockfile").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["dependencies", "targets", "version"],
            root.GetProperty("lockfile").GetProperty("mods").GetProperty("SML").EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lockfile").GetProperty("mods").GetProperty("SML").GetProperty("dependencies").ValueKind);
        Assert.Equal(["hash", "link"],
            root.GetProperty("lockfile").GetProperty("mods").GetProperty("SML").GetProperty("targets").GetProperty("Windows").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["gameVersion"], root.GetProperty("metadata").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_version_missing_from_the_catalogue_writes_neither_file()
    {
        var profile = Atria with { Mods = [.. Atria.Mods, new ProfileMod("Gone", "1.0.0")] };

        var e = await Assert.ThrowsAsync<InvalidProfileException>(() => Export(ProfileFormats.Both, profile));

        Assert.Contains("Gone 1.0.0 is not listed", e.Message);
        Assert.False(File.Exists(_dir.Combine("Atria.olp")));
        Assert.False(File.Exists(_dir.Combine("Atria.smmprofile")));
    }

    [Fact]
    public async Task A_mod_without_a_build_for_the_target_is_reported()
    {
        var e = await Assert.ThrowsAsync<InvalidProfileException>(() =>
            Exporter().ExportAsync(Atria, _dir.Combine("Server"), ProfileFormats.SmmProfile, 513885, TargetPlatform.LinuxServer));

        Assert.Contains("WeatherExtended 1.1.0 has no LinuxServer build", e.Message);
    }

    [Fact]
    public void Smmprofile_round_trip_keeps_unknown_fields()
    {
        const string original = """
            {
              "profile": { "mods": { "SML": { "version": ">=0.0.0", "enabled": true, "pinned": true } },
                           "name": "P", "required_targets": ["Windows"], "colour": "orange" },
              "lockfile": { "mods": { "SML": { "dependencies": null, "targets": {}, "version": "3.12.0" } }, "version": 1 },
              "metadata": { "gameVersion": 513885, "exportedBy": "something newer" },
              "futureSection": { "a": 1 }
            }
            """;
        var doc = SmmProfileFile.Parse(new MemoryStream(Encoding.UTF8.GetBytes(original)));
        var path = _dir.Combine("round.smmprofile");

        SmmProfileFile.Write(path, doc);
        var text = File.ReadAllText(path);

        foreach (var kept in new[] { "\"pinned\": true", "\"colour\": \"orange\"", "\"exportedBy\": \"something newer\"", "\"futureSection\"" })
            Assert.Contains(kept, text);
    }

    [Fact]
    public void Smmprofile_keys_are_written_in_character_code_order()
    {
        // Culture-aware sorting would put AutomatedInhalers first; other tools write AutoLink first.
        const string json = """
            { "profile": { "mods": { "AutomatedInhalers": { "version": ">=0.0.0", "enabled": true },
                                     "AutoLink": { "version": ">=0.0.0", "enabled": true } },
                           "name": "P", "required_targets": ["Windows"] },
              "lockfile": { "mods": {}, "version": 1 }, "metadata": { "gameVersion": 1 } }
            """;
        var path = _dir.Combine("order.smmprofile");

        SmmProfileFile.Write(path, SmmProfileFile.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json))));
        var text = File.ReadAllText(path);

        Assert.True(text.IndexOf("\"AutoLink\"", StringComparison.Ordinal) < text.IndexOf("\"AutomatedInhalers\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "profile": { "name": "P", "mods": {} } }""", "required")]
    [InlineData("""{ "profile": { "name": "P", "mods": {} }, "lockfile": { "mods": {}, "version": 2 } }""", "not supported")]
    [InlineData("""{ "profile": { "name": "P", "mods": { "bad name": { "version": ">=0.0.0" } } }, "lockfile": { "mods": {}, "version": 1 } }""", "not a valid mod reference")]
    [InlineData("""{ "profile": { "name": "P", "mods": {} }, "lockfile": { "mods": { "M": { "version": ">=1.0.0" } }, "version": 1 } }""", "invalid locked version")]
    public void Rejects_invalid_smmprofiles(string json, string expected)
    {
        var e = Assert.Throws<InvalidProfileException>(() => SmmProfileFile.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json))));
        Assert.Contains(expected, e.Message);
    }
}
