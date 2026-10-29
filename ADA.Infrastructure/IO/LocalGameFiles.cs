namespace ADA.Infrastructure.IO;

/// <summary>A game install on this machine's filesystem, including mounted network shares.</summary>
/// <param name="factoryGame">Absolute path of the install's <c>FactoryGame</c> folder.</param>
internal sealed class LocalGameFiles(string factoryGame) : IGameFiles
{
    public bool DirectoryExists(string path) => Directory.Exists(Resolve(path));

    public bool FileExists(string path) => File.Exists(Resolve(path));

    public void CreateDirectory(string path) => Directory.CreateDirectory(Resolve(path));

    // Same drive, so this is a rename. If Mods were a link to another drive this throws instead of
    // copying, and the transaction rolls back.
    public void MoveDirectory(string from, string to) => Directory.Move(Resolve(from), Resolve(to));

    public void DeleteDirectory(string path)
    {
        var folder = Resolve(path);
        // Read-only files (common on Windows) would otherwise stop the delete.
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
        Directory.Delete(folder, recursive: true);
    }

    public IEnumerable<string> DirectoryNames(string path) =>
        Directory.EnumerateDirectories(Resolve(path)).Select(Path.GetFileName).OfType<string>();

    public IEnumerable<GameFileInfo> Files(string path)
    {
        var folder = Resolve(path);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var info = new System.IO.FileInfo(file);
            yield return new GameFileInfo(Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'),
                info.Length, info.LastWriteTimeUtc);
        }
    }

    public GameFileInfo? FileInfo(string path)
    {
        var info = new System.IO.FileInfo(Resolve(path));
        return info.Exists ? new GameFileInfo(path, info.Length, info.LastWriteTimeUtc) : null;
    }

    public Stream OpenRead(string path) => File.OpenRead(Resolve(path));

    public Stream CreateNew(string path) => new DurableFileStream(Resolve(path));

    public void ReplaceFile(string from, string to) => File.Move(Resolve(from), Resolve(to), overwrite: true);

    public void DeleteFile(string path) => File.Delete(Resolve(path));

    public void RequireFreeSpace(long needed, string purpose) => DiskSpace.Require(factoryGame, needed, purpose);

    public IDisposable AcquireLock(string path) =>
        new FileStream(Resolve(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    public void MarkHidden(string path)
    {
        // The leading dot hides it on macOS and Linux; Windows needs the attribute.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var info = new DirectoryInfo(Resolve(path));
        if (!info.Attributes.HasFlag(FileAttributes.Hidden))
        {
            info.Attributes |= FileAttributes.Hidden;
        }
    }

    /// <exception cref="ArgumentException">The path would leave <c>FactoryGame</c>.</exception>
    private string Resolve(string path)
    {
        if (!GamePaths.IsSafe(path))
        {
            throw new ArgumentException($"'{path}' is not a path inside FactoryGame.", nameof(path));
        }
        return path.Length == 0 ? factoryGame : Path.Combine(factoryGame, path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>A new file that is flushed through to the disk when it's closed, not just to the OS.</summary>
    private sealed class DurableFileStream(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Flush(flushToDisk: true);
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
