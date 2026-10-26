namespace ADA.Infrastructure.Settings;

/// <summary>The downloaded <c>.smod</c> files kept in <c>Staging/Archives/</c>.</summary>
public sealed class ArchiveCache(string archivesFolder)
{
    public string Folder { get; } = archivesFolder;

    public (int Count, long Bytes) Measure()
    {
        if (!Directory.Exists(Folder))
            return (0, 0);

        var count = 0;
        long bytes = 0;
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*.smod", SearchOption.AllDirectories))
        {
            count++;
            bytes += file.Length;
        }
        return (count, bytes);
    }

    /// <summary>Deletes every kept archive. Installed mods are unaffected; they can be re-downloaded.</summary>
    public void DeleteAll()
    {
        if (!Directory.Exists(Folder))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(Folder))
        {
            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
    }
}
