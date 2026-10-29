using System.Net;
using System.Text;
using ADA.Core.Games;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Servers;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Servers;

public sealed class SatisfactoryServerApiTests
{
    [Theory]
    [InlineData("/home/steam/SatisfactoryDedicatedServer/", "/home/steam/SatisfactoryDedicatedServer")]
    [InlineData("  /opt/sds  ", "/opt/sds")]
    [InlineData(@"C:\SatisfactoryServer", "/C:/SatisfactoryServer")]
    [InlineData("c:/Games/Server/", "/c:/Games/Server")]
    [InlineData("/", "/")]
    public void ServerFoldersAreWrittenTheWaySftpExpects(string entered, string expected)
    {
        Assert.Equal(expected, ServerSetupCheck.NormalisedRoot(entered));
    }

    [Fact]
    public async Task TheServerApiCountsAnyAnswerAsRunningAndSendsAHealthCheck()
    {
        var http = new FakeHttpHandler().Respond("""{ "data": { "health": "healthy" } }""");

        Assert.True(await new SatisfactoryServerApi(new HttpClient(http)).IsRunningAsync("192.168.1.20", 7777));
        Assert.Contains("\"function\":\"HealthCheck\"", Assert.Single(http.RequestBodies));
    }

    [Fact]
    public async Task NoAnswerOrAnErrorMeansNotRunning()
    {
        var refused = new FakeHttpHandler().Throw(new HttpRequestException("Connection refused."));
        var error = new FakeHttpHandler().Fail(HttpStatusCode.InternalServerError);

        Assert.False(await new SatisfactoryServerApi(new HttpClient(refused)).IsRunningAsync("192.168.1.20", 7777));
        Assert.False(await new SatisfactoryServerApi(new HttpClient(error)).IsRunningAsync("192.168.1.20", 7777));
    }

    [Fact]
    public void RunningMessagesSayToStopAServerAndToCloseTheGame()
    {
        var server = new GameInstall("sftp://abc", GameLauncher.Unknown, GameHost.Linux, GameKind.DedicatedServer, null, "Home");
        var local = new GameInstall("/games/Satisfactory", GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "Steam");

        Assert.StartsWith("The server is running. Stop it", GameRunningException.For(server).Message);
        Assert.StartsWith("Satisfactory is running. Close the game", GameRunningException.For(local).Message);
    }

    [Fact]
    public void BuildsAreReadFromAVersionFileStream()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("""{ "Changelist": 513885, "GameVersion": "1.2.4.1" }"""));
        using var broken = new MemoryStream(Encoding.UTF8.GetBytes("not json"));

        Assert.Equal(new GameBuild(513885, "1.2.4.1"), GameBuild.FromVersionFile(file));
        Assert.Null(GameBuild.FromVersionFile(broken));
    }
}
