using System.Text;
using ADA.Core.Profiles;
using ADA.Infrastructure.Profiles;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Profiles;

public sealed class OlpFileTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static ModProfile Parse(string json) => OlpFile.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ReadsTheDocumentedExample()
    {
        var profile = Parse("""
            {
              "name": "Atria",
              "mods": [
                { "name": "SML", "version": "3.12.0" },
                { "name": "WeatherExtended", "version": "1.1.0" }
              ]
            }
            """);

        Assert.Equal("Atria", profile.Name);
        Assert.Equal([new ProfileMod("SML", "3.12.0"), new ProfileMod("WeatherExtended", "1.1.0")], profile.Mods);
    }

    [Fact]
    public void WritesSortedIndentedAndIdenticalEveryTime()
    {
        var profile = new ModProfile("Atria", [new ProfileMod("WeatherExtended", "1.1.0"), new ProfileMod("SML", "3.12.0")]);
        var first = _dir.Combine("a.olp");
        var second = _dir.Combine("b.olp");

        OlpFile.Write(first, profile);
        OlpFile.Write(second, profile with { Mods = profile.Mods.Reverse().ToList() });

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        Assert.Equal("""
            {
              "name": "Atria",
              "mods": [
                {
                  "name": "SML",
                  "version": "3.12.0"
                },
                {
                  "name": "WeatherExtended",
                  "version": "1.1.0"
                }
              ]
            }
            """.ReplaceLineEndings("\n") + "\n", File.ReadAllText(first).ReplaceLineEndings("\n"));
        Assert.Equal(["SML", "WeatherExtended"], OlpFile.Read(first).Mods.Select(m => m.Name));
    }

    [Fact]
    public void WritesNamesAsTypedWithoutHtmlEscaping()
    {
        var path = _dir.Combine("p.olp");
        OlpFile.Write(path, new ModProfile("Ryan's <Mega> & Co", []));

        Assert.Contains("\"name\": \"Ryan's <Mega> & Co\"", File.ReadAllText(path));
        Assert.Equal("Ryan's <Mega> & Co", OlpFile.Read(path).Name);
    }

    [Fact]
    public void EmptyModListIsAllowedAndUnknownFieldsAreIgnored()
    {
        var profile = Parse("""{ "name": " Empty ", "mods": [], "notes": "from a newer ADA" }""");

        Assert.Equal("Empty", profile.Name);
        Assert.Empty(profile.Mods);
    }

    [Theory]
    [InlineData("3.12.0")]
    [InlineData("0.0.1")]
    [InlineData("1.2.0-beta.1")]
    [InlineData("10.20.30+build.5")]
    public void AcceptsExactVersions(string version) =>
        Assert.Equal(version, Parse($$"""{ "name": "P", "mods": [ { "name": "M", "version": "{{version}}" } ] }""").Mods[0].Version);

    [Theory]
    [InlineData("""not json""", "Not a valid profile")]
    [InlineData("""null""", "empty")]
    [InlineData("""{ "mods": [] }""", "no name")]
    [InlineData("""{ "name": "  ", "mods": [] }""", "no name")]
    [InlineData("""{ "name": "P" }""", "no \"mods\"")]
    [InlineData("""{ "name": "P", "mods": [ null ] }""", "is empty")]
    [InlineData("""{ "name": "P", "mods": [ { "version": "1.0.0" } ] }""", "invalid name")]
    [InlineData("""{ "name": "P", "mods": [ { "name": "Weather Extended", "version": "1.0.0" } ] }""", "invalid name")]
    [InlineData("""{ "name": "P", "mods": [ { "name": "../evil", "version": "1.0.0" } ] }""", "invalid name")]
    [InlineData("""{ "name": "P", "mods": [ { "name": "M" } ] }""", "invalid version")]
    [InlineData("""{ "name": "P", "mods": [ { "name": "M", "version": ">=1.0.0" } ] }""", "invalid version")]
    [InlineData("""{ "name": "P", "mods": [ { "name": "M", "version": "1.0" } ] }""", "invalid version")]
    [InlineData("""{ "name": "P", "mods": [ { "name": "M", "version": "01.0.0" } ] }""", "invalid version")]
    [InlineData("""{ "name": "P", "mods": [ { "name": "SML", "version": "1.0.0" }, { "name": "sml", "version": "2.0.0" } ] }""", "more than once")]
    public void RejectsFilesThatBreakTheRules(string json, string expectedMessage)
    {
        var e = Assert.Throws<InvalidProfileException>(() => Parse(json));
        Assert.Contains(expectedMessage, e.Message);
    }

    [Fact]
    public void RejectsOversizedProfiles()
    {
        var tooLong = new string('x', OlpFile.MAX_NAME_LENGTH + 1);
        Assert.Throws<InvalidProfileException>(() => Parse($$"""{ "name": "{{tooLong}}", "mods": [] }"""));

        var mods = string.Join(",", Enumerable.Range(0, OlpFile.MAX_MODS + 1).Select(i => $$"""{ "name": "M{{i}}", "version": "1.0.0" }"""));
        Assert.Throws<InvalidProfileException>(() => Parse($$"""{ "name": "P", "mods": [ {{mods}} ] }"""));

        var big = _dir.WriteFile("big.olp", $$"""{ "name": "P", "mods": [], "pad": "{{new string('x', (int)OlpFile.MAX_FILE_BYTES)}}" }""");
        Assert.Throws<InvalidProfileException>(() => OlpFile.Read(big));
    }

    [Fact]
    public void WillNotWriteAnInvalidProfile()
    {
        var path = _dir.Combine("bad.olp");

        Assert.Throws<InvalidProfileException>(() => OlpFile.Write(path, new ModProfile("P", [new ProfileMod("M", ">=1.0.0")])));
        Assert.False(File.Exists(path));
    }
}
