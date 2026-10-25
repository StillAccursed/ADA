namespace Overlord.Core.Games;

/// <summary>
/// Second half of the anti-piracy rule: a launcher record alone is not enough, the folder it points
/// at must also look like a real Satisfactory install.
/// </summary>
public static class GameInstallValidator
{
    private static readonly string[] KnownExecutables =
    [
        "FactoryGameSteam.exe",
        "FactoryGameEGS.exe",
        "FactoryGame.exe",
        "FactoryServer.exe",
        "FactoryServer.sh",
    ];

    public static bool LooksLikeGame(string gamePath) => InspectKind(gamePath) is not null;

    /// <returns>The kind of install at <paramref name="gamePath"/>, or null if it is not one.</returns>
    public static GameKind? InspectKind(string gamePath)
    {
        if (!Directory.Exists(Path.Combine(gamePath, "FactoryGame", "Binaries")))
            return null;

        var exe = KnownExecutables.FirstOrDefault(e => File.Exists(Path.Combine(gamePath, e)));
        return exe switch
        {
            null => null,
            _ when exe.StartsWith("FactoryServer", StringComparison.Ordinal) => GameKind.DedicatedServer,
            _ => GameKind.Client,
        };
    }
}
