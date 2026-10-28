namespace ADA.Infrastructure.Settings;

/// <summary>The downloaded <c>.smod</c> files kept in <c>Staging/Archives/</c>.</summary>
public sealed class ArchiveCache(string archivesFolder)
{
    /// <summary>Raised on the deleting thread after <see cref="DeleteAll"/>, even when it deleted only some archives.</summary>
    public event EventHandler? Changed;

    public string Folder { get; } = archivesFolder;

    /// <summary>
    /// Count and total size of every <c>.smod</c> in the folder and its subfolders; (0, 0) when the
    /// folder doesn't exist.
    /// </summary>
    public (int Count, long Bytes) Measure()
    {
        if (!Directory.Exists(Folder))
        {
            return (0, 0);
        }

        var count = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*.smod", SearchOption.AllDirectories))
        {
            count++;
            bytes += file.Length;
        }
        return (count, bytes);
    }

    /// <summary>
    /// Deletes every kept archive, then raises <see cref="Changed"/>. Installed mods are unaffected;
    /// they can be re-downloaded.
    /// </summary>
    /// <exception cref="IOException">An archive couldn't be deleted; earlier ones stay deleted.</exception>
    public void DeleteAll()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(Folder))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
