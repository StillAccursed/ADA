using ADA.Platform.Formats;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Formats;

public class WineRegistryFileTests
{
    // Shape copied from a real CrossOver system.reg: escaped backslashes, timestamp after the key,
    // a #time line, and non-string values mixed in.
    private const string Hive = """
        WINE REGISTRY Version 2
        ;; All keys relative to \\Machine

        [Software\\Epic Games\\EpicGamesLauncher] 1792336927
        #time=1dd5f146fed09c4
        "AppDataPath"="C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data\\"
        "Flags"=dword:00000001

        [Software\\Wow6432Node\\Valve\\Steam] 1792336927
        "InstallPath"="C:\\Program Files (x86)\\Steam"
        "Quoted"="say \"hi\""
        """;

    [Fact]
    public void Reads_string_value_and_unescapes_it()
    {
        using var dir = new TempDir();
        var reg = dir.WriteFile("system.reg", Hive);

        Assert.Equal(@"C:\ProgramData\Epic\EpicGamesLauncher\Data\",
            WineRegistryFile.ReadString(reg, @"Software\Epic Games\EpicGamesLauncher", "AppDataPath"));
        Assert.Equal(@"C:\Program Files (x86)\Steam",
            WineRegistryFile.ReadString(reg, @"software\wow6432node\valve\steam", "installpath"));
        Assert.Equal("say \"hi\"",
            WineRegistryFile.ReadString(reg, @"Software\Wow6432Node\Valve\Steam", "Quoted"));
    }

    [Fact]
    public void Ignores_values_under_other_keys_and_non_string_values()
    {
        using var dir = new TempDir();
        var reg = dir.WriteFile("system.reg", Hive);

        Assert.Null(WineRegistryFile.ReadString(reg, @"Software\Valve\Steam", "InstallPath"));
        Assert.Null(WineRegistryFile.ReadString(reg, @"Software\Epic Games\EpicGamesLauncher", "Flags"));
        Assert.Null(WineRegistryFile.ReadString(dir.Combine("missing.reg"), "x", "y"));
    }
}
