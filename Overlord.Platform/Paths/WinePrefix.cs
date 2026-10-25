namespace Overlord.Platform.Paths;

/// <summary>
/// A Wine prefix: a CrossOver bottle on macOS, or a Proton/Heroic prefix on Linux. Translates the
/// Windows paths that launchers write inside it into real host paths.
/// </summary>
public sealed class WinePrefix(string prefixPath)
{
    public string PrefixPath { get; } = prefixPath;

    public string SystemRegistry => Path.Combine(PrefixPath, "system.reg");

    public string UserRegistry => Path.Combine(PrefixPath, "user.reg");

    /// <summary>
    /// Maps e.g. <c>Y:\Games\Satisfactory</c> to the host folder. Drive letters are resolved
    /// through <c>dosdevices/&lt;letter&gt;:</c> symlinks, so installs on mapped drives (which
    /// CrossOver creates for the user's home folder) are found, not only those in <c>drive_c</c>.
    /// </summary>
    /// <returns>The existing host path, or null if the drive is unmapped or the path is missing.</returns>
    public string? ToHostPath(string windowsPath)
    {
        if (windowsPath.Length < 2 || windowsPath[1] != ':' || !char.IsAsciiLetter(windowsPath[0]))
            return null;

        var drive = char.ToLowerInvariant(windowsPath[0]);
        var root = DriveRoot(drive);
        if (root is null)
            return null;

        var segments = windowsPath[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return CaseInsensitivePath.Resolve(root, segments);
    }

    private string? DriveRoot(char drive)
    {
        var device = Path.Combine(PrefixPath, "dosdevices", $"{drive}:");
        if (Directory.Exists(device))
        {
            // Return the link's real target so the UI shows /Users/me/Games/... rather than
            // .../dosdevices/y:/Games/...
            try
            {
                return new DirectoryInfo(device).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? device;
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
