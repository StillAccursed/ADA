using ADA.Platform.Formats;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Formats;

public class GameBuildReaderTests
{
    // Trimmed from a real Epic install's Engine/Binaries/Win64/FactoryGameEGS-Win64-Shipping.version.
    private const string VERSION_FILE = """
        {
        	"MajorVersion": 5,
        	"MinorVersion": 6,
        	"Changelist": 513885,
        	"BranchName": "++FactoryGame+rel-main-anniversary-2026",
        	"BuildId": "513885",
        	"GameVersion": "1.2.4.1"
        }
        """;

    [Fact]
    public void Reads_changelist_and_game_version()
    {
        using var dir = new TempDir();
        var game = dir.CreateGame("Satisfactory");
        dir.WriteFile("Satisfactory/Engine/Binaries/Win64/FactoryGameEGS-Win64-Shipping.version", VERSION_FILE);

        var build = GameBuildReader.Read(game);

        Assert.NotNull(build);
        Assert.Equal(513885, build.Changelist);
        Assert.Equal("1.2.4.1", build.GameVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{ "Changelist": 0 }""")]
    [InlineData("""{ "GameVersion": "1.0" }""")]
    public void Returns_null_when_build_cannot_be_read(string? content)
    {
        using var dir = new TempDir();
        var game = dir.CreateGame("Satisfactory");
        if (content is not null)
        {
            dir.WriteFile("Satisfactory/Engine/Binaries/Win64/FactoryGameEGS-Win64-Shipping.version", content);
        }

        Assert.Null(GameBuildReader.Read(game));
    }
}
