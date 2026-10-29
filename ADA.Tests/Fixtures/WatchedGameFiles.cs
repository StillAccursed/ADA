using ADA.Infrastructure.IO;

namespace ADA.Tests.Fixtures;

/// <summary>
/// Local game files that record which files are read and can misreport a file's size, for tests
/// of what the install engine checks after writing.
/// </summary>
internal sealed class WatchedGameFiles(string factoryGame) : IGameFiles
{
    private readonly LocalGameFiles _inner = new(factoryGame);

    /// <summary>Every path opened for reading, in order.</summary>
    public List<string> Reads { get; } = [];

    /// <summary>Reports files whose path ends with this as one byte shorter than they are; null reports sizes truly.</summary>
    public string? ShortenEnding { get; set; }

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    public bool FileExists(string path) => _inner.FileExists(path);

    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public void MoveDirectory(string from, string to) => _inner.MoveDirectory(from, to);

    public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);

    public IEnumerable<string> DirectoryNames(string path) => _inner.DirectoryNames(path);

    public IEnumerable<GameFileInfo> Files(string path) => _inner.Files(path);

    public GameFileInfo? FileInfo(string path) => _inner.FileInfo(path) is { } info && ShortenEnding is { } ending && path.EndsWith(ending, StringComparison.Ordinal)
        ? info with { Length = info.Length - 1 }
        : _inner.FileInfo(path);

    public Stream OpenRead(string path)
    {
        Reads.Add(path);
        return _inner.OpenRead(path);
    }

    public Stream CreateNew(string path) => _inner.CreateNew(path);

    public void ReplaceFile(string from, string to) => _inner.ReplaceFile(from, to);

    public void DeleteFile(string path) => _inner.DeleteFile(path);

    public void RequireFreeSpace(long needed, string purpose) => _inner.RequireFreeSpace(needed, purpose);

    public IDisposable AcquireLock(string path) => _inner.AcquireLock(path);

    public void MarkHidden(string path) => _inner.MarkHidden(path);
}
