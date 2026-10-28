namespace ADA.Infrastructure.IO;

/// <summary>Bytes measured so far; incomplete results must not be presented as an exact total.</summary>
public sealed record FolderMeasurement(long Bytes, bool IsComplete, string? Problem = null);

/// <summary>Capacity of one mounted volume; null fields mean it could not be queried.</summary>
public sealed record VolumeMeasurement(string? Name, long? AvailableBytes, long? TotalBytes);

/// <summary>Read-only snapshot of the selected game's mods and ADA's shared downloaded archives.</summary>
public sealed record DashboardStorageSnapshot(FolderMeasurement Mods, FolderMeasurement Archives,
    VolumeMeasurement GameVolume, VolumeMeasurement ArchiveVolume);

/// <summary>Measures folders without following links or touching transaction/recovery data.</summary>
public sealed class DashboardStorageReader(string archivesFolder)
{
    /// <summary>Runs filesystem traversal on a worker thread. Cancellation discards an obsolete selection's scan.</summary>
    public Task<DashboardStorageSnapshot> ReadAsync(string? modsFolder, CancellationToken ct = default) => Task.Run(() =>
        new DashboardStorageSnapshot(
            modsFolder is null ? new(0, false, "No installation selected.") : MeasureFolder(modsFolder, false, ct),
            MeasureFolder(archivesFolder, true, ct),
            MeasureVolume(modsFolder), MeasureVolume(archivesFolder)), ct);

    /// <summary>Missing folders are empty; denied/disconnected folders are incomplete. Links are skipped.</summary>
    internal static FolderMeasurement MeasureFolder(string folder, bool archivesOnly, CancellationToken ct = default)
    {
        long bytes = 0;
        string? problem = null;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var path))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    problem = "The folder path points to a file.";
                    continue;
                }
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                        if (entry is DirectoryInfo)
                        {
                            pending.Push(entry.FullName);
                        }
                        else if (!archivesOnly || entry.Extension.Equals(".smod", StringComparison.OrdinalIgnoreCase))
                        {
                            bytes += ((FileInfo)entry).Length;
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        problem = e.Message;
                    }
                }
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Missing root is empty only while its volume/parent is still accessible.
                if (path != folder || !HasAccessibleAncestor(folder))
                {
                    problem = "The folder or its drive is unavailable.";
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problem = e.Message;
            }
        }
        return new(bytes, problem is null, problem);
    }

    private static bool HasAccessibleAncestor(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (OperatingSystem.IsMacOS() && full.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            var volume = full.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2);
            if (!Directory.Exists("/" + string.Join('/', volume)))
            {
                return false;
            }
        }
        var parent = Directory.GetParent(full);
        while (parent is not null)
        {
            if (parent.Exists)
            {
                return true;
            }
            parent = parent.Parent;
        }
        return false;
    }

    private static VolumeMeasurement MeasureVolume(string? path)
    {
        try
        {
            if (path is not null && DiskSpace.DriveFor(path) is { } drive)
            {
                return new(drive.Name, drive.AvailableFreeSpace, drive.TotalSize);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Optional drive telemetry must not discard successfully measured folders.
        }
        return new(null, null, null);
    }
}
