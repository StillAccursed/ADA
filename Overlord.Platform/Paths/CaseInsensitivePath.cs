namespace Overlord.Platform.Paths;

/// <summary>
/// Windows paths recorded by launchers ignore case, but Linux filesystems (and some macOS volumes)
/// do not. This walks a path one segment at a time, matching each against what is actually on disk.
/// </summary>
public static class CaseInsensitivePath
{
    /// <returns>
    /// The existing host path spelled as it is on disk, or null if any segment is missing. The
    /// on-disk spelling matters even on case-insensitive volumes (APFS, NTFS), where a plain
    /// existence check would succeed but keep the launcher's casing, so the same folder could
    /// later appear under two different paths.
    /// </returns>
    public static string? Resolve(string root, IEnumerable<string> segments)
    {
        if (!Directory.Exists(root))
            return null;

        var current = root;
        foreach (var segment in segments)
        {
            if (!Directory.Exists(current))
                return null;

            string? caseless = null;
            string? exact = null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, segment, StringComparison.Ordinal))
                {
                    exact = entry;
                    break;
                }
                if (caseless is null && string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
                    caseless = entry;
            }

            current = exact ?? caseless;
            if (current is null)
                return null;
        }

        return current;
    }
}
