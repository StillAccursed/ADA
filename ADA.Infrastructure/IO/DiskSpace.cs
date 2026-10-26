namespace ADA.Infrastructure.IO;

internal static class DiskSpace
{
    /// <summary>The drive holding <paramref name="path"/> (the longest matching mount point), or null if unknown.</summary>
    public static DriveInfo? DriveFor(string path)
    {
        var full = Path.GetFullPath(path);
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            .MaxBy(d => d.RootDirectory.FullName.Length);
    }

    /// <exception cref="IOException">The drive has less than <paramref name="needed"/> bytes free.</exception>
    public static void Require(string path, long needed, string purpose)
    {
        if (DriveFor(path) is { } drive && drive.AvailableFreeSpace < needed)
        {
            throw new InsufficientSpaceException(
                $"Not enough free space: {purpose} need {Format(needed)} on {drive.Name}, which has {Format(drive.AvailableFreeSpace)} free.");
        }
    }

    // MB below 10 GB, GB above: "17.2 GB" reads better than "17,612 MB" for a large profile.
    private static string Format(long bytes) => bytes >= 10L * 1024 * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):N1} GB"
        : $"{bytes / (1024 * 1024):N0} MB";
}

public sealed class InsufficientSpaceException(string message) : IOException(message);
