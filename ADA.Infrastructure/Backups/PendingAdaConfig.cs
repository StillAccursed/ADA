namespace ADA.Infrastructure.Backups;

/// <summary>
/// ADA config restored from a backup waits in a staging folder, because the running app's stores cache what
/// they read and would write over restored files. This moves it into place at startup, before any store is
/// created.
/// </summary>
public static class PendingAdaConfig
{
    /// <summary>
    /// Moves every file in <paramref name="pendingFolder"/> over its counterpart in <paramref name="configFolder"/>,
    /// then removes the pending folder. Files only in <paramref name="configFolder"/> are kept.
    /// </summary>
    /// <returns>How many files were moved; 0 when nothing was pending.</returns>
    public static int Apply(string pendingFolder, string configFolder)
    {
        if (!Directory.Exists(pendingFolder))
        {
            return 0;
        }

        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(pendingFolder, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(configFolder, Path.GetRelativePath(pendingFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(file, destination, overwrite: true);
            moved++;
        }
        Directory.Delete(pendingFolder, recursive: true);
        return moved;
    }
}
