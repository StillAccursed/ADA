using ADA.Core.Games;

namespace ADA.Platform.Detection;

/// <summary>Picks the <see cref="IGameDetector"/> for the OS ADA is running on.</summary>
public static class GameDetection
{
    /// <summary>The detector for the OS ADA is running on.</summary>
    public static IGameDetector ForCurrentOS()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDetector();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new CrossOverDetector();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxDetector();
        }

        throw new PlatformNotSupportedException("ADA supports Windows, Linux and macOS.");
    }
}
