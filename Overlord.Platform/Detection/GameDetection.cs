using Overlord.Core.Games;

namespace Overlord.Platform.Detection;

public static class GameDetection
{
    /// <summary>The detector for the OS Overlord is running on.</summary>
    public static IGameDetector ForCurrentOS()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsDetector();
        if (OperatingSystem.IsMacOS())
            return new CrossOverDetector();
        if (OperatingSystem.IsLinux())
            return new LinuxDetector();

        throw new PlatformNotSupportedException("Overlord supports Windows, Linux and macOS.");
    }
}
