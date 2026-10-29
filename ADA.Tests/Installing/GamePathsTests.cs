using ADA.Infrastructure.IO;

namespace ADA.Tests.Installing;

public sealed class GamePathsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Mods")]
    [InlineData("Mods/GameFeatures/A")]
    [InlineData(".ada/txn-1/new/A")]
    [InlineData("Content//A.pak")]
    public void PathsInsideTheInstallAreSafe(string path)
    {
        Assert.True(GamePaths.IsSafe(path));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("Mods/../x")]
    [InlineData("./x")]
    [InlineData("/x")]
    [InlineData("Mods\\A")]
    [InlineData("C:x")]
    public void PathsThatCouldLeaveTheInstallAreNotSafe(string path)
    {
        Assert.False(GamePaths.IsSafe(path));
    }

    [Theory]
    [InlineData("", "Mods", "Mods")]
    [InlineData("Mods", "A", "Mods/A")]
    [InlineData(".ada/txn-1", "new/A", ".ada/txn-1/new/A")]
    public void CombineJoinsWithForwardSlashes(string parent, string child, string expected)
    {
        Assert.Equal(expected, GamePaths.Combine(parent, child));
    }

    [Theory]
    [InlineData("Mods/GameFeatures/A", "Mods/GameFeatures")]
    [InlineData("Mods", "")]
    [InlineData("", "")]
    public void ParentIsTheContainingFolder(string path, string expected)
    {
        Assert.Equal(expected, GamePaths.Parent(path));
    }
}
