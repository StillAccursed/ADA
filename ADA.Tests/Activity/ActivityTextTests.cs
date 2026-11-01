using ADA.Infrastructure.Activity;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Logging;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Activity;

public class ActivityTextTests
{
    private static InstalledMod Mod(string reference, string version) =>
        new(reference, version, false, $"Mods/{reference}", DateTimeOffset.UnixEpoch, []);

    [Fact]
    public void DescribesNewUpdatedReinstalledAndRemovedMods()
    {
        var result = new TransactionResult(
            [Mod("SML", "3.12.0"), Mod("AutoTrains", "1.3.8"), Mod("Same", "1.0.0")],
            ["Old"],
            new Dictionary<string, string> { ["SML"] = "3.11.0", ["Same"] = "1.0.0", ["Old"] = "2.0.0" });

        var change = ActivityText.DescribeChange(result);

        Assert.Equal("Installed 1 mod, updated 1, reinstalled 1, removed 1", change.Summary);
        Assert.Equal(["AutoTrains 1.3.8", "Same 1.0.0 (reinstalled)", "SML 3.11.0 → 3.12.0", "Removed Old 2.0.0"], change.Details);
    }

    [Theory]
    [InlineData(2, 0, "Installed 2 mods")]
    [InlineData(0, 3, "Removed 3 mods")]
    [InlineData(0, 0, "No mods changed")]
    public void SummariesNameOnlyWhatHappened(int installed, int removed, string expected)
    {
        var result = new TransactionResult(
            Enumerable.Range(0, installed).Select(i => Mod($"New{i}", "1.0.0")).ToList(),
            Enumerable.Range(0, removed).Select(i => $"Gone{i}").ToList());

        Assert.Equal(expected, ActivityText.DescribeChange(result).Summary);
    }

    [Fact]
    public void DescribesWhatAFailedChangeSetOutToDo()
    {
        Assert.Equal(["Install A 1.0.0", "Remove B"],
            ActivityText.DescribeIntent([CatalogFixtures.Version("A", "1.0.0")], ["B"]));
    }

    [Fact]
    public void LongListsAreSampledInLogLines()
    {
        Assert.Equal("a, b", LogText.Sample(["a", "b"]));
        Assert.Equal("0, 1, 2, 3, 4, 5, 6, 7, 8, 9 and 276 more",
            LogText.Sample(Enumerable.Range(0, 286).Select(i => i.ToString())));
    }
}
