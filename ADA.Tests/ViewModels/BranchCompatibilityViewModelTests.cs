using ADA.Core.Catalog;
using ADA.Tests.Fixtures;
using ADA.ViewModels;

namespace ADA.Tests.ViewModels;

public sealed class BranchCompatibilityViewModelTests
{
    [Theory]
    [InlineData(CompatibilityState.Works, CompatibilityState.Broken, "✓", "✕")]
    [InlineData(CompatibilityState.Broken, CompatibilityState.Works, "✕", "✓")]
    [InlineData(CompatibilityState.Damaged, CompatibilityState.Unknown, "✕", "✕")]
    [InlineData(CompatibilityState.Unknown, CompatibilityState.Damaged, "✕", "✕")]
    public void BranchIndicatorsReflectTheirOwnApiReport(
        CompatibilityState stable, CompatibilityState experimental, string stableGlyph, string experimentalGlyph)
    {
        var mod = new ModItemViewModel(CatalogFixtures.Mod("Example") with
        {
            CompatibilityRelease = stable,
            CompatibilityExperimental = experimental,
        });

        Assert.Equal(stableGlyph, mod.StableCompatibility.Glyph);
        Assert.Equal(experimentalGlyph, mod.ExperimentalCompatibility.Glyph);
        Assert.Equal(stableGlyph == "✓", mod.StableCompatibility.IsCompatible);
        Assert.Equal(experimentalGlyph == "✓", mod.ExperimentalCompatibility.IsCompatible);
    }

    [Fact]
    public void ChoosingAnOlderVersionDoesNotChangeCurrentBranchReports()
    {
        var mod = new ModItemViewModel(CatalogFixtures.Mod("Example") with
        {
            CompatibilityRelease = CompatibilityState.Works,
            CompatibilityExperimental = CompatibilityState.Broken,
        });
        var stable = mod.StableCompatibility;
        var experimental = mod.ExperimentalCompatibility;

        mod.InstalledVersion = "1.0.0";
        mod.SelectedVersionChoice = new Option<string?>("0.9.0", "0.9.0");

        Assert.Same(stable, mod.StableCompatibility);
        Assert.Same(experimental, mod.ExperimentalCompatibility);
        Assert.True(mod.StableCompatibility.IsCompatible);
        Assert.False(mod.ExperimentalCompatibility.IsCompatible);
        Assert.Equal("Compatible", mod.StableCompatibility.Description);
        Assert.Equal("Not compatible", mod.ExperimentalCompatibility.Description);
    }

    [Fact]
    public void CrossTooltipsDistinguishUnreportedCompatibilityFromPartialBreakage()
    {
        var mod = new ModItemViewModel(CatalogFixtures.Mod("Example") with
        {
            CompatibilityRelease = CompatibilityState.Unknown,
            CompatibilityExperimental = CompatibilityState.Damaged,
        });

        Assert.Equal("✕", mod.StableCompatibility.Glyph);
        Assert.Equal("Compatibility not reported", mod.StableCompatibility.Description);
        Assert.Equal("✕", mod.ExperimentalCompatibility.Glyph);
        Assert.Equal("Partly broken", mod.ExperimentalCompatibility.Description);
    }
}
