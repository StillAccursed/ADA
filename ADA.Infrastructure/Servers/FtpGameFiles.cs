using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;

namespace ADA.Infrastructure.Servers;

/// <summary>
/// A server's game install over FTPS. FTP can't create a file only if it's missing, so
/// <see cref="CreateNew"/> checks first; that's safe because every write happens under the
/// install's lock, which uses a folder (see <see cref="FtpLockFile"/>). Files are read and written
/// through a temporary file on this PC, so no FTP data connection stays open between calls.
/// </summary>
/// <param name="session">A connected session, owned by <see cref="ServerConnections"/>; never disposed here.</param>
/// <param name="gameRoot">Absolute path on the server of the folder containing <c>FactoryGame</c>.</param>
internal sealed class FtpGameFiles(IFtpSession session, string gameRoot, ILogger log) : IGameFiles
{
    private readonly string _factoryGame = $"{gameRoot.TrimEnd('/')}/FactoryGame";

    public bool DirectoryExists(string path) => session.DirectoryExists(Resolve(path));

    public bool FileExists(string path) => session.FileExists(Resolve(path));

    public void CreateDirectory(string path) => session.CreateDirectories(Resolve(path));

    public void MoveDirectory(string from, string to)
    {
        var target = Resolve(to);
        if (session.DirectoryExists(target) || session.FileExists(target))
        {
            throw new IOException($"{to} already exists.");
        }
        session.Rename(Resolve(from), target);
    }

    public void DeleteDirectory(string path) => session.DeleteDirectory(Resolve(path));

    public IEnumerable<string> DirectoryNames(string path) =>
        session.List(Resolve(path)).Where(e => e.IsDirectory).Select(e => e.Name).ToList();

    public IEnumerable<GameFileInfo> Files(string path)
    {
        var found = new List<GameFileInfo>();
        Collect(Resolve(path), "", found);
        return found;
    }

    public GameFileInfo? FileInfo(string path) =>
        session.Stat(Resolve(path)) is { } stat ? new GameFileInfo(path, stat.Size, stat.ModifiedUtc) : null;

    public Stream OpenRead(string path) => Download(session, Resolve(path));

    /// <exception cref="IOException">The file already exists.</exception>
    public Stream CreateNew(string path)
    {
        var remote = Resolve(path);
        if (session.FileExists(remote))
        {
            throw new IOException($"{path} already exists.");
        }
        return new UploadOnClose(session, remote, onFailure: null);
    }

    public void ReplaceFile(string from, string to) => session.Rename(Resolve(from), Resolve(to));

    public void DeleteFile(string path) => session.DeleteFile(Resolve(path));

    public void RequireFreeSpace(long needed, string purpose)
    {
        // FTP has no standard way to ask for free space; the install fails cleanly if the disk fills.
    }

    public IDisposable AcquireLock(string path) => RemoteLock.Acquire(new FtpLockFile(session, Resolve(path)), log);

    public void MarkHidden(string path)
    {
        // The leading dot hides it on Linux; Windows servers show it, which is harmless.
    }

    /// <summary>The whole remote file, in a temporary file deleted when the stream closes.</summary>
    internal static Stream Download(IFtpSession session, string remote)
    {
        var temp = TempFile();
        try
        {
            session.Download(remote, temp);
            temp.Position = 0;
            return temp;
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }

    internal static FileStream TempFile() => new(Path.Combine(Path.GetTempPath(), $"ada-ftp-{Guid.NewGuid():N}"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);

    /// <exception cref="ArgumentException">The path would leave <c>FactoryGame</c>.</exception>
    private string Resolve(string path)
    {
        if (!GamePaths.IsSafe(path))
        {
            throw new ArgumentException($"'{path}' is not a path inside FactoryGame.", nameof(path));
        }
        return path.Length == 0 ? _factoryGame : $"{_factoryGame}/{path}";
    }

    private void Collect(string remote, string relative, List<GameFileInfo> found)
    {
        foreach (var entry in session.List(remote))
        {
            var path = GamePaths.Combine(relative, entry.Name);
            if (entry.IsDirectory)
            {
                Collect($"{remote}/{entry.Name}", path, found);
            }
            // Each file's time comes from MDTM, the same as FileInfo, so records made from a listing
            // (taking over found mods) match the fast check later.
            else if (session.Stat($"{remote}/{entry.Name}") is { } stat)
            {
                found.Add(new GameFileInfo(path, stat.Size, stat.ModifiedUtc));
            }
        }
    }
}

/// <summary>
/// A stream written to a temporary file on this PC and uploaded when it's disposed, so the file
/// appears on the server whole or not at all (the engine then writes it under a temporary name and
/// renames it into place). An upload failure is thrown from <see cref="Dispose(bool)"/>.
/// </summary>
/// <param name="onFailure">Called before the failure is thrown, e.g. to remove a lock folder made for this file.</param>
internal sealed class UploadOnClose(IFtpSession session, string remote, Action? onFailure) : Stream
{
    private readonly FileStream _buffer = FtpGameFiles.TempFile();
    private bool _closed;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_closed;

    public override long Length => _buffer.Length;

    public override long Position
    {
        get => _buffer.Position;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _buffer.Write(buffer);

    public override void Flush() => _buffer.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            try
            {
                _buffer.Position = 0;
                session.Upload(_buffer, remote);
            }
            catch
            {
                onFailure?.Invoke();
                throw;
            }
            finally
            {
                _buffer.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A lock on an FTP server. FTP can't create a file only if it's missing, but creating a folder
/// fails if it exists on every server, so the lock is the folder <c>{path}.d</c> with the holder
/// written inside.
/// </summary>
internal sealed class FtpLockFile(IFtpSession session, string path) : ILockFile
{
    private readonly string _folder = path + ".d";

    private string Holder => _folder + "/holder.json";

    public string Key => $"ftp:{session.Key}{_folder}";

    public Stream CreateNew()
    {
        session.MakeDirectory(_folder);
        // If the holder can't be written, the folder goes too, so the lock isn't left nameless.
        return new UploadOnClose(session, Holder, onFailure: () => TryDelete());
    }

    /// <returns>Null when there's no lock; an empty stream for a lock folder without a holder.</returns>
    public Stream? OpenRead()
    {
        if (session.FileExists(Holder))
        {
            return FtpGameFiles.Download(session, Holder);
        }
        return session.DirectoryExists(_folder) ? new MemoryStream() : null;
    }

    public void Delete() => session.DeleteDirectory(_folder);

    private void TryDelete()
    {
        try
        {
            session.DeleteDirectory(_folder);
        }
        catch (IOException)
        {
            // The original failure is what gets reported.
        }
    }
}
