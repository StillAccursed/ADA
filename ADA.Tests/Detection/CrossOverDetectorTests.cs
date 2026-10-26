using ADA.Core.Games;
using ADA.Platform.Detection;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Detection;

public class CrossOverDetectorTests
{
    private const string SystemReg = """
        WINE REGISTRY Version 2

        [Software\\Epic Games\\EpicGamesLauncher] 1792336927
        "AppDataPath"="C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data\\"
        """;

    private static string EpicItem(string installLocation, bool incomplete = false, string ns = "crab") => $$"""
        {
          "DisplayName": "Satisfactory",
          "AppName": "CrabEA",
          "CatalogNamespace": "{{ns}}",
          "InstallLocation": "{{installLocation.Replace(@"\", @"\\")}}",
          "AppVersionString": "513885.383",
          "bIsIncompleteInstall": {{(incomplete ? "true" : "false")}}
        }
        """;

    /// <summary>Mirrors a real setup: Epic in a bottle, game on Y:, which maps to the user's home.</summary>
    private static (TempDir Dir, string Game) EpicBottleWithGameOnMappedDrive(string item)
    {
        var dir = new TempDir();
        var home = Directory.CreateDirectory(dir.Combine("home")).FullName;
        var game = dir.CreateGame("home/Games/Satisfactory");

        var bottle = dir.Combine("Bottles", "Epic Games Store");
        dir.WriteFile("Bottles/Epic Games Store/system.reg", SystemReg);
        dir.WriteFile("Bottles/Epic Games Store/drive_c/ProgramData/Epic/EpicGamesLauncher/Data/Manifests/ABC.item", item);
        Directory.CreateDirectory(Path.Combine(bottle, "dosdevices"));
        Directory.CreateSymbolicLink(Path.Combine(bottle, "dosdevices", "y:"), home);
        Directory.CreateSymbolicLink(Path.Combine(bottle, "dosdevices", "c:"), Path.Combine(bottle, "drive_c"));
        return (dir, game);
    }

    [UnixFact]
    public void Finds_epic_install_on_mapped_drive()
    {
        var (dir, game) = EpicBottleWithGameOnMappedDrive(EpicItem(@"Y:\Games\Satisfactory"));
        using var _ = dir;
        dir.WriteFile("home/Games/Satisfactory/Engine/Binaries/Win64/FactoryGameEGS-Win64-Shipping.version",
            """{ "Changelist": 513885, "GameVersion": "1.2.4.1" }""");

        var install = Assert.Single(new CrossOverDetector(dir.Combine("Bottles")).Detect());

        Assert.Equal(game, install.GamePath);
        Assert.Equal(GameLauncher.Epic, install.Launcher);
        Assert.Equal(GameHost.CrossOver, install.Host);
        Assert.Equal(GameKind.Client, install.Kind);
        Assert.Equal("513885.383", install.Version);
        Assert.Contains("Epic Games Store", install.Location);
        Assert.Equal(513885, install.Build?.Changelist);
    }

    [UnixFact]
    public void Skips_incomplete_installs()
    {
        var (dir, _) = EpicBottleWithGameOnMappedDrive(EpicItem(@"Y:\Games\Satisfactory", incomplete: true));
        using var _d = dir;

        Assert.Empty(new CrossOverDetector(dir.Combine("Bottles")).Detect());
    }

    [UnixFact]
    public void Skips_other_games()
    {
        var (dir, _) = EpicBottleWithGameOnMappedDrive(EpicItem(@"Y:\Games\Satisfactory", ns: "fortnite"));
        using var _d = dir;

        Assert.Empty(new CrossOverDetector(dir.Combine("Bottles")).Detect());
    }

    [UnixFact]
    public void Skips_manifest_whose_folder_is_not_a_game()
    {
        var (dir, game) = EpicBottleWithGameOnMappedDrive(EpicItem(@"Y:\Games\Satisfactory"));
        using var _d = dir;
        File.Delete(Path.Combine(game, "FactoryGameEGS.exe"));

        Assert.Empty(new CrossOverDetector(dir.Combine("Bottles")).Detect());
    }

    [Fact]
    public void Finds_steam_install_in_second_library_and_ignores_partial_download()
    {
        using var dir = new TempDir();
        dir.WriteFile("Bottles/Steam/system.reg", """
            [Software\\Wow6432Node\\Valve\\Steam] 1
            "InstallPath"="C:\\Program Files (x86)\\Steam"
            """);
        dir.WriteFile("Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/libraryfolders.vdf", """
            "libraryfolders" { "0" { "path" "C:\\Program Files (x86)\\Steam" } "1" { "path" "C:\\Games\\SteamLibrary" } }
            """);
        // Client fully installed in library 1; server only partially downloaded in library 0.
        dir.WriteFile("Bottles/Steam/drive_c/Games/SteamLibrary/steamapps/appmanifest_526870.acf", """
            "AppState" { "appid" "526870" "StateFlags" "4" "installdir" "Satisfactory" "buildid" "17000000" }
            """);
        var game = dir.CreateGame("Bottles/Steam/drive_c/Games/SteamLibrary/steamapps/common/Satisfactory", "FactoryGameSteam.exe");
        dir.WriteFile("Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/appmanifest_1690800.acf", """
            "AppState" { "appid" "1690800" "StateFlags" "1026" "installdir" "SatisfactoryDedicatedServer" }
            """);
        dir.CreateGame("Bottles/Steam/drive_c/Program Files (x86)/Steam/steamapps/common/SatisfactoryDedicatedServer", "FactoryServer.exe");

        var install = Assert.Single(new CrossOverDetector(dir.Combine("Bottles")).Detect());

        Assert.Equal(game, install.GamePath);
        Assert.Equal(GameLauncher.Steam, install.Launcher);
        Assert.Equal("17000000", install.Version);
    }

    [Fact]
    public void Missing_bottles_folder_finds_nothing()
    {
        using var dir = new TempDir();
        Assert.Empty(new CrossOverDetector(dir.Combine("nope")).Detect());
    }
}
