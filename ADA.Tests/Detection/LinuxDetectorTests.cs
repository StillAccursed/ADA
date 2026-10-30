using ADA.Core.Games;
using ADA.Platform.Detection;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Detection;

public class LinuxDetectorTests
{
    private const string STEAM_ROOT = "home/.local/share/Steam";

    private const string SECOND_LIBRARY = "home/Games/SteamLibrary";

    private const string PROTON_LOCAL_APP_DATA = "steamapps/compatdata/526870/pfx/drive_c/users/steamuser/AppData/Local/FactoryGame";

    /// <summary>A Steam root whose libraryfolders.vdf lists itself and a second library.</summary>
    private static void WriteSteam(TempDir dir)
    {
        Directory.CreateDirectory(dir.Combine(SECOND_LIBRARY.Split('/')));
        dir.WriteFile($"{STEAM_ROOT}/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders" { "0" { "path" "{{dir.Combine(STEAM_ROOT.Split('/'))}}" } "1" { "path" "{{dir.Combine(SECOND_LIBRARY.Split('/'))}}" } }
            """);
    }

    private static void WriteSteamApp(TempDir dir, string library, string appId, string installDir, string executable)
    {
        dir.WriteFile($"{library}/steamapps/appmanifest_{appId}.acf", $$"""
            "AppState" { "appid" "{{appId}}" "StateFlags" "4" "installdir" "{{installDir}}" }
            """);
        dir.CreateGame($"{library}/steamapps/common/{installDir}", executable);
    }

    private static string Expected(TempDir dir, string library) =>
        dir.Combine([.. library.Split('/'), .. PROTON_LOCAL_APP_DATA.Split('/')]);

    [UnixFact]
    public void ProtonClientUsesPrefixInItsOwnLibrary()
    {
        using var dir = new TempDir();
        WriteSteam(dir);
        WriteSteamApp(dir, SECOND_LIBRARY, "526870", "Satisfactory", "FactoryGameSteam.exe");
        Directory.CreateDirectory(dir.Combine($"{SECOND_LIBRARY}/steamapps/compatdata/526870/pfx".Split('/')));
        Directory.CreateDirectory(dir.Combine($"{STEAM_ROOT}/steamapps/compatdata/526870/pfx".Split('/')));

        var install = Assert.Single(new LinuxDetector(dir.Combine("home")).Detect());

        Assert.Equal(Expected(dir, SECOND_LIBRARY), install.UserDataPath);
    }

    [UnixFact]
    public void ProtonClientFallsBackToPrefixInSteamRoot()
    {
        using var dir = new TempDir();
        WriteSteam(dir);
        WriteSteamApp(dir, SECOND_LIBRARY, "526870", "Satisfactory", "FactoryGameSteam.exe");
        Directory.CreateDirectory(dir.Combine($"{STEAM_ROOT}/steamapps/compatdata/526870/pfx".Split('/')));

        var install = Assert.Single(new LinuxDetector(dir.Combine("home")).Detect());

        Assert.Equal(Expected(dir, STEAM_ROOT), install.UserDataPath);
    }

    [UnixFact]
    public void ProtonClientNeverLaunchedUsesItsOwnLibrary()
    {
        using var dir = new TempDir();
        WriteSteam(dir);
        WriteSteamApp(dir, SECOND_LIBRARY, "526870", "Satisfactory", "FactoryGameSteam.exe");

        var install = Assert.Single(new LinuxDetector(dir.Combine("home")).Detect());

        Assert.Equal(Expected(dir, SECOND_LIBRARY), install.UserDataPath);
    }

    [UnixFact]
    public void NativeServerUsesConfigEpicFolder()
    {
        using var dir = new TempDir();
        WriteSteam(dir);
        WriteSteamApp(dir, STEAM_ROOT, "1690800", "SatisfactoryDedicatedServer", "FactoryServer.sh");

        var install = Assert.Single(new LinuxDetector(dir.Combine("home")).Detect());

        Assert.Equal(GameHost.Linux, install.Host);
        Assert.Equal(dir.Combine("home", ".config", "Epic", "FactoryGame"), install.UserDataPath);
    }

    /// <summary>Heroic's Legendary records the game; Heroic's GamesConfig records its prefix.</summary>
    private static string WriteHeroicGame(TempDir dir, string winePrefix)
    {
        var game = dir.CreateGame("home/Games/Heroic/Satisfactory");
        dir.WriteFile("home/.config/heroic/legendaryConfig/legendary/installed.json", $$"""
            { "CrabEA": { "install_path": "{{game}}", "version": "513885" } }
            """);
        dir.WriteFile("home/.config/heroic/GamesConfig/CrabEA.json", $$"""
            { "CrabEA": { "winePrefix": "{{winePrefix}}" }, "version": "v0" }
            """);
        return game;
    }

    [UnixFact]
    public void HeroicGameUsesItsConfiguredWinePrefix()
    {
        using var dir = new TempDir();
        var prefix = dir.Combine("home", "Games", "Heroic", "Prefixes", "Satisfactory");
        WriteHeroicGame(dir, prefix);
        var local = Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "users", "alice", "AppData", "Local")).FullName;
        File.WriteAllText(Path.Combine(prefix, "user.reg"), """
            [Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Folders] 1
            "Local AppData"="C:\\users\\alice\\AppData\\Local"
            """);

        var install = Assert.Single(new LinuxDetector(dir.Combine("home")).Detect());

        Assert.Equal(Path.Combine(local, "FactoryGame"), install.UserDataPath);
    }

    [UnixFact]
    public void HeroicProtonPrefixUsesPfx()
    {
        using var dir = new TempDir();
        var prefix = dir.Combine("home", "Games", "Heroic", "Prefixes", "Satisfactory");
        WriteHeroicGame(dir, prefix);
        Directory.CreateDirectory(Path.Combine(prefix, "pfx"));

        var install = Assert.Single(new LinuxDetector(dir.Combine("home")).Detect());

        Assert.Equal(Path.Combine(prefix, "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "FactoryGame"),
            install.UserDataPath);
    }

    [UnixFact]
    public void HeroicGameWithoutGamesConfigHasNoUserData()
    {
        using var dir = new TempDir();
        WriteHeroicGame(dir, "unused");
        File.Delete(dir.Combine("home", ".config", "heroic", "GamesConfig", "CrabEA.json"));

        Assert.Null(Assert.Single(new LinuxDetector(dir.Combine("home")).Detect()).UserDataPath);
    }

    [UnixFact]
    public void LegendaryWithoutHeroicHasNoUserData()
    {
        using var dir = new TempDir();
        var game = dir.CreateGame("home/Games/Satisfactory");
        dir.WriteFile("home/.config/legendary/installed.json", $$"""
            { "CrabEA": { "install_path": "{{game}}" } }
            """);

        var install = Assert.Single(new LinuxDetector(dir.Combine("home")).Detect());

        Assert.Equal(game, install.GamePath);
        Assert.Null(install.UserDataPath);
    }
}
