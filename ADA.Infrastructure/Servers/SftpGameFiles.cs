using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ADA.Infrastructure.Servers;

/// <summary>
/// A server's game install over SFTP. Errors from SSH.NET are turned into <see cref="IOException"/>
/// or <see cref="UnauthorizedAccessException"/>, which the install engine's rollback and cleanup
/// already handle; SSH.NET's own exception types would get past them.
/// </summary>
/// <param name="client">A connected client, owned by <see cref="ServerConnections"/>; never disposed here.</param>
/// <param name="gameRoot">Absolute path on the server of the folder containing <c>FactoryGame</c>.</param>
internal sealed class SftpGameFiles(SftpClient client, string gameRoot, ILogger log) : IGameFiles
{
    private readonly string _factoryGame = $"{gameRoot.TrimEnd('/')}/FactoryGame";

    public bool DirectoryExists(string path) => Run(() => Attributes(Resolve(path))?.IsDirectory == true);

    public bool FileExists(string path) => Run(() => Attributes(Resolve(path))?.IsRegularFile == true);

    public void CreateDirectory(string path) => Run(() =>
    {
        Resolve(path);
        // SFTP makes one folder at a time; FactoryGame itself is known to exist.
        var current = _factoryGame;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = $"{current}/{segment}";
            if (Attributes(current) is null)
            {
                client.CreateDirectory(current);
            }
        }
    });

    public void MoveDirectory(string from, string to) => Run(() =>
    {
        var target = Resolve(to);
        // SFTP's plain rename refuses an existing target, but not every server follows the spec.
        if (Attributes(target) is not null)
        {
            throw new IOException($"{to} already exists.");
        }
        client.RenameFile(Resolve(from), target);
    });

    public void DeleteDirectory(string path) => Run(() => DeleteTree(Resolve(path)));

    public IEnumerable<string> DirectoryNames(string path) => Run(() =>
        Entries(Resolve(path)).Where(e => e.IsDirectory).Select(e => e.Name).ToList());

    public IEnumerable<GameFileInfo> Files(string path) => Run(() =>
    {
        var found = new List<GameFileInfo>();
        Collect(Resolve(path), "", found);
        return found;
    });

    public GameFileInfo? FileInfo(string path) => Run(() =>
        Attributes(Resolve(path)) is { IsRegularFile: true } a ? new GameFileInfo(path, a.Size, a.LastWriteTimeUtc) : null);

    public Stream OpenRead(string path) => Run(() => (Stream)client.OpenRead(Resolve(path)));

    // Exclusive create (SSH_FXF_CREAT | SSH_FXF_EXCL). Disposing closes the handle, which returns
    // once the server has written the data; there's no fsync over SSH.NET.
    public Stream CreateNew(string path) => Run(() => (Stream)client.Open(Resolve(path), FileMode.CreateNew, FileAccess.Write));

    public void ReplaceFile(string from, string to) => Run(() => client.RenameFile(Resolve(from), Resolve(to), isPosix: true));

    public void DeleteFile(string path) => Run(() =>
    {
        try
        {
            client.DeleteFile(Resolve(path));
        }
        catch (SftpPathNotFoundException)
        {
            // Already gone, which is what was asked for.
        }
    });

    public void RequireFreeSpace(long needed, string purpose)
    {
        long available;
        try
        {
            var status = client.GetStatus(_factoryGame);
            available = (long)Math.Min(status.AvailableBlocks * status.BlockSize, long.MaxValue);
        }
        catch (Exception e) when (e is SshException or NotSupportedException)
        {
            log.LogWarning("The server can't report free space ({Reason}); installing without the check", e.Message);
            return;
        }
        if (available < needed)
        {
            throw new InsufficientSpaceException(
                $"Not enough free space: {purpose} need {DiskSpace.Format(needed)} on the server, which has {DiskSpace.Format(available)} free.");
        }
    }

    public IDisposable AcquireLock(string path) => Run(() => RemoteLock.Acquire(client, Resolve(path), log));

    public void MarkHidden(string path)
    {
        // The leading dot hides it on Linux; Windows servers show it, which is harmless.
    }

    /// <exception cref="ArgumentException">The path would leave <c>FactoryGame</c>.</exception>
    private string Resolve(string path)
    {
        if (!GamePaths.IsSafe(path))
        {
            throw Unsafe(path);
        }
        return path.Length == 0 ? _factoryGame : $"{_factoryGame}/{path}";
    }

    private static ArgumentException Unsafe(string path) => new($"'{path}' is not a path inside FactoryGame.", nameof(path));

    /// <returns>Null when nothing is at <paramref name="remote"/>.</returns>
    private Renci.SshNet.Sftp.SftpFileAttributes? Attributes(string remote)
    {
        try
        {
            return client.GetAttributes(remote);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    private IEnumerable<Renci.SshNet.Sftp.ISftpFile> Entries(string remote) =>
        client.ListDirectory(remote).Where(e => e.Name is not ("." or ".."));

    private void Collect(string remote, string relative, List<GameFileInfo> found)
    {
        foreach (var entry in Entries(remote))
        {
            var path = GamePaths.Combine(relative, entry.Name);
            if (entry.IsDirectory)
            {
                Collect($"{remote}/{entry.Name}", path, found);
            }
            else if (entry.IsRegularFile)
            {
                found.Add(new GameFileInfo(path, entry.Length, entry.LastWriteTimeUtc));
            }
        }
    }

    /// <summary>SFTP only removes empty folders, so this empties them from the bottom up.</summary>
    private void DeleteTree(string remote)
    {
        foreach (var entry in Entries(remote).ToList())
        {
            var child = $"{remote}/{entry.Name}";
            if (entry.IsDirectory)
            {
                DeleteTree(child);
            }
            else
            {
                client.DeleteFile(child);
            }
        }
        client.DeleteDirectory(remote);
    }

    private static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SftpPermissionDeniedException e)
        {
            throw new UnauthorizedAccessException($"The server refused access: {e.Message}", e);
        }
        catch (SftpPathNotFoundException e)
        {
            throw new FileNotFoundException(e.Message, e);
        }
        catch (Exception e) when (e is SshException or NotSupportedException)
        {
            throw new ServerUnavailableException($"The server stopped answering: {e.Message}", e);
        }
    }

    private static void Run(Action action) => Run(() =>
    {
        action();
        return true;
    });
}
