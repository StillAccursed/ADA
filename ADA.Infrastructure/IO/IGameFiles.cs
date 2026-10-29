namespace ADA.Infrastructure.IO;

/// <summary>Size and modification time of one file, as the fast check compares them.</summary>
/// <param name="Path">Relative to the folder the listing started from, <c>/</c> separators.</param>
internal sealed record GameFileInfo(string Path, long Length, DateTime LastWriteUtc);

/// <summary>
/// The files of one game install as the install engine sees them, on a local disk or a remote
/// server. Every path is relative to <c>FactoryGame</c> (see <see cref="GamePaths"/>).
/// </summary>
/// <remarks>
/// Methods block and may do network I/O; the engine already runs inside <c>Task.Run</c>. Failures
/// are <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, which the engine's
/// rollback and cleanup handling already expect. A path that isn't <see cref="GamePaths.IsSafe"/>
/// throws <see cref="ArgumentException"/>.
/// </remarks>
internal interface IGameFiles
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>Creates the folder and any missing parents. Does nothing if it exists.</summary>
    void CreateDirectory(string path);

    /// <summary>
    /// Renames a folder in one step. Throws if <paramref name="to"/> already exists, and never
    /// copies instead: a commit is only crash-safe while every move is a rename.
    /// </summary>
    void MoveDirectory(string from, string to);

    /// <summary>Deletes a folder and everything in it, including read-only files.</summary>
    void DeleteDirectory(string path);

    /// <summary>The names of the folders directly inside <paramref name="path"/>.</summary>
    IEnumerable<string> DirectoryNames(string path);

    /// <summary>Every file under <paramref name="path"/>, at any depth, with paths relative to it.</summary>
    IEnumerable<GameFileInfo> Files(string path);

    /// <returns>Null when there's no file at <paramref name="path"/>.</returns>
    GameFileInfo? FileInfo(string path);

    Stream OpenRead(string path);

    /// <summary>
    /// Creates a file that must not exist yet. Disposing the stream makes what was written durable
    /// before it returns, so a crash afterwards can't leave the file short.
    /// </summary>
    Stream CreateNew(string path);

    /// <summary>Moves <paramref name="from"/> over <paramref name="to"/> in one step, replacing it if it exists.</summary>
    void ReplaceFile(string from, string to);

    /// <summary>Deletes the file. Does nothing if it doesn't exist.</summary>
    void DeleteFile(string path);

    /// <summary>Throws when the install's filesystem can't take <paramref name="needed"/> more bytes.</summary>
    /// <param name="purpose">Completes "Not enough free space: {purpose} need …".</param>
    /// <exception cref="InsufficientSpaceException">There isn't room.</exception>
    void RequireFreeSpace(long needed, string purpose);

    /// <summary>
    /// Holds the install's exclusive lock until the result is disposed, so two ADA windows (or,
    /// for a server, two PCs) never change the same install at once.
    /// </summary>
    /// <exception cref="IOException">Someone else holds it.</exception>
    IDisposable AcquireLock(string path);

    /// <summary>Hides a folder where the OS needs an attribute for that (Windows). Does nothing elsewhere.</summary>
    void MarkHidden(string path);
}
