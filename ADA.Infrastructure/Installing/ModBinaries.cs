using ADA.Core.Catalog;

namespace ADA.Infrastructure.Installing;

/// <summary>
/// Tells which platform a mod's compiled library was built for, from its file name. Unreal names a
/// module after the target that loads it, so client and Windows server builds differ only by prefix
/// even though both live in <c>Binaries/Win64</c> (checked against SML 3.12.0's three builds).
/// </summary>
internal static class ModBinaries
{
    /// <summary>The folders under a mod's <c>Binaries</c> that hold libraries.</summary>
    public static readonly string[] FOLDERS = ["Win64", "Linux"];

    private const string CLIENT_PREFIX = "FactoryGame"; // FactoryGameSteam-… and FactoryGameEGS-…
    private const string SERVER_PREFIX = "FactoryServer-";
    private const string LINUX_SERVER_PREFIX = "libFactoryServer-";

    /// <summary>Whether <paramref name="fileName"/> is a library (not a symbol or module list) at all.</summary>
    public static bool IsLibrary(string fileName) =>
        fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".so", StringComparison.Ordinal);

    /// <summary>Whether the library <paramref name="fileName"/> loads on <paramref name="platform"/>.</summary>
    public static bool IsFor(string fileName, TargetPlatform platform) => platform switch
    {
        TargetPlatform.Windows => fileName.StartsWith(CLIENT_PREFIX, StringComparison.Ordinal)
                                  && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
        TargetPlatform.WindowsServer => fileName.StartsWith(SERVER_PREFIX, StringComparison.Ordinal)
                                        && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
        TargetPlatform.LinuxServer => fileName.StartsWith(LINUX_SERVER_PREFIX, StringComparison.Ordinal)
                                      && fileName.EndsWith(".so", StringComparison.Ordinal),
        _ => false,
    };
}
