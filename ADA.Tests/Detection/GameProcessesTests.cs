using ADA.Platform.Detection;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Detection;

public class GameProcessesTests
{
    [Theory]
    // Seen under CrossOver (ps comm column) while the game was running, 2026-10-25.
    [InlineData(@"Y:/Games/Satisfactory/FactoryGameEGS.exe")]
    [InlineData(@"Y:\Games\Satisfactory\Engine\Binaries\Win64\FactoryGameEGS-Win64-Shipping.exe")]
    // Windows process names, and Linux's 15-character truncation.
    [InlineData("FactoryGameSteam-Win64-Shipping")]
    [InlineData("FactoryGameStea")]
    [InlineData("FactoryServer-Linux-Shipping")]
    [InlineData("/opt/SatisfactoryDedicatedServer/FactoryServer.sh")]
    [InlineData("  factorygameegs.exe  ")]
    public void RecognisesTheGameInEveryForm(string name) =>
        Assert.True(GameProcesses.IsGameProcessName(name));

    [Theory]
    [InlineData(@"Y:\Games\Satisfactory\FactoryGame\Plugins\Sentry\Binaries\Win64\crashpad_handler.exe")]
    [InlineData("EOSOverlayRenderer-Win64-Shipping.exe")]
    [InlineData("/Applications/Satisfactory Mod Manager.app/Contents/MacOS/FactoryGameHelper-not/ADA")]
    [InlineData("wine64-preloader")]
    [InlineData("")]
    public void IgnoresOtherProcesses(string name) =>
        Assert.False(GameProcesses.IsGameProcessName(name));

    [Fact]
    public void ParsesPsLinesIncludingNamesWithSpaces()
    {
        Assert.Equal(new GameProcess(12462, @"Y:\Games\Satisfactory\Engine\Binaries\Win64\FactoryGameEGS-Win64-Shipping.exe"),
            GameProcesses.ParsePsLine(@"12462 Y:\Games\Satisfactory\Engine\Binaries\Win64\FactoryGameEGS-Win64-Shipping.exe"));
        Assert.Equal(new GameProcess(7, "C:/Program Files/Epic Games/Satisfactory/FactoryGameEGS.exe"),
            GameProcesses.ParsePsLine("    7 C:/Program Files/Epic Games/Satisfactory/FactoryGameEGS.exe  "));
        Assert.Null(GameProcesses.ParsePsLine("12494 Y:\\Games\\Satisfactory\\FactoryGame\\Plugins\\Sentry\\crashpad_handler.exe"));
        Assert.Null(GameProcesses.ParsePsLine("not-a-pid FactoryGame.exe"));
        Assert.Null(GameProcesses.ParsePsLine(""));
    }

    [UnixFact]
    public async Task ClosesARunningProcessAndReportsItGone()
    {
        // A stand-in for the game: `sleep` started through a link named like it, so only this
        // process is touched. (A copy of a system binary is killed on launch by macOS.)
        using var dir = new TempDir();
        var fake = dir.Combine("FactoryGameTest");
        File.CreateSymbolicLink(fake, "/bin/sleep");
        using var process = System.Diagnostics.Process.Start(fake, "300");

        var found = (await GameProcesses.FindRunningAsync()).Where(p => p.Id == process.Id).ToList();
        Assert.Single(found);

        Assert.True(await GameProcesses.CloseAsync(found, TimeSpan.FromSeconds(5)));
        Assert.True(process.HasExited);
    }
}
