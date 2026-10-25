using Overlord.Core.Resolution;

namespace Overlord.Tests.Resolution;

public class GameVersionRangeTests
{
    [Theory]
    [InlineData(">=513885", 513885, true)]
    [InlineData(">=513885", 513884, false)]
    [InlineData(">513885", 513885, false)]
    [InlineData("<520000", 513885, true)]
    [InlineData("<=513885", 513885, true)]
    [InlineData("=513885", 513885, true)]
    [InlineData("513885", 513886, false)]
    [InlineData(">=500000 <520000", 513885, true)]
    [InlineData(">=500000 <510000", 513885, false)]
    [InlineData("<400000 || >=513885", 513885, true)]
    [InlineData("*", 1, true)]
    [InlineData("", 1, true)]
    public void Matches_game_builds(string text, int changelist, bool expected)
    {
        Assert.True(GameVersionRange.TryParse(text, out var range));
        Assert.Equal(expected, range.Contains(changelist));
    }

    [Theory]
    [InlineData(">=abc")]
    [InlineData("^1.2.0")]
    [InlineData(">=1.2")]
    public void Rejects_ranges_that_are_not_over_build_numbers(string text) =>
        Assert.False(GameVersionRange.TryParse(text, out _));
}
