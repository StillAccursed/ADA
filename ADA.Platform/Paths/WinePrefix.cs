namespace ADA.Platform.Paths;

/// <summary>
/// Translates Windows launcher paths inside a Wine prefix into host paths.
/// </summary>
internal sealed class WinePrefix(string prefixPath)
{
    public string PrefixPath { get; } = prefixPath;

    public string SystemRegistry => Path.Combine(PrefixPath, "system.reg");

    public string UserRegistry => Path.Combine(PrefixPath, "user.reg");

    /// <summary>
    /// Resolves mapped drives through dosdevices symlinks, with drive_c
    /// as the fallback for C:. Matches path segments case-insensitively.
    /// </summary>
    /// <returns>
    /// The existing host path, or null if the drive is unmapped or the path is missing.
    /// </returns>
    public string? ToHostPath(string windowsPath)
    {
        if (windowsPath.Length < 2 ||
            windowsPath[1] != ':' ||
            !char.IsAsciiLetter(windowsPath[0]))
        {
            return null;
        }

        var drive = char.ToLowerInvariant(windowsPath[0]);
        var root = DriveRoot(drive);
        if (root is null)
        {
            return null;
        }

        var segments = windowsPath[2..].Split(
            ['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        return CaseInsensitivePath.Resolve(root, segments);
    }

    private string? DriveRoot(char drive)
    {
        // Combine would discard the prefix on Windows because "c:" is rooted.
        var device = Path.Join(PrefixPath, "dosdevices", $"{drive}:");
        if (Directory.Exists(device))
        {
            // Resolve the link so the UI displays the actual host location.
            try
            {
                return new DirectoryInfo(device)
                    .ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? device;
            }
            catch (IOException)
            {
                return device;
            }
        }

        var driveC = Path.Combine(PrefixPath, "drive_c");
        return drive == 'c' && Directory.Exists(driveC) ? driveC : null;
    }
}
