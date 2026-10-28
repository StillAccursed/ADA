using ADA.Core.Catalog;

namespace ADA.Tests.Core;

public class ModIdentifiersTests
{
    [Theory]
    [InlineData("WeatherExtended", true)]
    [InlineData("SML", true)]
    [InlineData("Mod_2", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("Weather\") { evil }", false)]
    [InlineData(null, false)]
    public void References_are_plain_identifiers(string? reference, bool valid) =>
        Assert.Equal(valid, ModIdentifiers.IsValidReference(reference));

    [Fact]
    public void References_are_at_most_100_characters()
    {
        Assert.True(ModIdentifiers.IsValidReference(new string('a', ModIdentifiers.MAX_REFERENCE_LENGTH)));
        Assert.False(ModIdentifiers.IsValidReference(new string('a', ModIdentifiers.MAX_REFERENCE_LENGTH + 1)));
    }

    [Theory]
    [InlineData("1.1.0", true)]
    [InlineData("0.0.1", true)]
    [InlineData("2.0.0-beta.1", true)]
    [InlineData("1.0.0+build.5", true)]
    [InlineData("1.0", false)]
    [InlineData("01.0.0", false)]
    [InlineData(">=1.0.0", false)]
    [InlineData("^3.12.0", false)]
    [InlineData(null, false)]
    public void Exact_versions_are_strict_semver(string? version, bool valid) =>
        Assert.Equal(valid, ModIdentifiers.IsExactVersion(version));
}
