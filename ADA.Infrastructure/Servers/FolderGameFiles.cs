using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;

namespace ADA.Infrastructure.Servers;

/// <summary>
/// A folder server's files: an ordinary folder on this PC, possibly on a network share another PC
/// also uses. Everything is local file access except the lock, which is a lock file like an SFTP
/// server's, because <c>FileShare.None</c> isn't reliably passed between PCs by every share.
/// </summary>
internal sealed class FolderGameFiles(string factoryGame, ILogger log) : IGameFiles
{
    private readonly LocalGameFiles _local = new(factoryGame);

    public bool DirectoryExists(string path) => _local.DirectoryExists(path);

    public bool FileExists(string path) => _local.FileExists(path);

    public void CreateDirectory(string path) => _local.CreateDirectory(path);

    public void MoveDirectory(string from, string to) => _local.MoveDirectory(from, to);

    public void DeleteDirectory(string path) => _local.DeleteDirectory(path);

    public IEnumerable<string> DirectoryNames(string path) => _local.DirectoryNames(path);

    public IEnumerable<GameFileInfo> Files(string path) => _local.Files(path);

    public GameFileInfo? FileInfo(string path) => _local.FileInfo(path);

    public Stream OpenRead(string path) => _local.OpenRead(path);

    public Stream CreateNew(string path) => _local.CreateNew(path);

    public void ReplaceFile(string from, string to) => _local.ReplaceFile(from, to);

    public void DeleteFile(string path) => _local.DeleteFile(path);

    public void RequireFreeSpace(long needed, string purpose) => _local.RequireFreeSpace(needed, purpose);

    /// <exception cref="LockHeldException">Another PC or operation holds it.</exception>
    public IDisposable AcquireLock(string path) => RemoteLock.Acquire(LockFile(factoryGame, path), log);

    public void MarkHidden(string path) => _local.MarkHidden(path);

    /// <summary>Free space where the folder is; null when the OS can't tell (e.g. a UNC path on Windows).</summary>
    internal long? AvailableFreeSpace() => DiskSpace.DriveFor(factoryGame)?.AvailableFreeSpace;

    /// <summary>The lock file for <paramref name="path"/>, relative to <paramref name="factoryGame"/>.</summary>
    /// <exception cref="ArgumentException">The path would leave <c>FactoryGame</c>.</exception>
    internal static FolderLockFile LockFile(string factoryGame, string path)
    {
        if (!GamePaths.IsSafe(path) || path.Length == 0)
        {
            throw new ArgumentException($"'{path}' is not a path inside FactoryGame.", nameof(path));
        }
        return new FolderLockFile(Path.Combine(factoryGame, path.Replace('/', Path.DirectorySeparatorChar)));
    }
}
