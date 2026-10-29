using Renci.SshNet;
using Renci.SshNet.Common;

namespace ADA.Infrastructure.Servers;

/// <summary>
/// Read and probe access to a server's folders, over SFTP or on this PC, for the setup check and
/// for reading the game build. Paths are full paths in the folders' own form.
/// </summary>
/// <remarks>Failures are <see cref="IOException"/>, whatever the transport.</remarks>
internal interface IServerFolders
{
    /// <summary>Why <see cref="Replace"/> failing means ADA can't keep its records safe here.</summary>
    string ReplaceUnsupported { get; }

    /// <summary>Joins a folder and a relative path with the separator these folders use.</summary>
    string Combine(string folder, string relative);

    bool IsDirectory(string path);

    /// <summary>The file's size; null when there's no file there.</summary>
    long? FileSize(string path);

    /// <summary>The folders directly inside <paramref name="path"/>, as full paths; none when it can't be listed.</summary>
    IReadOnlyList<string> Folders(string path);

    /// <summary>The names of the files directly inside <paramref name="path"/>; none when it can't be listed.</summary>
    IReadOnlyList<string> FileNames(string path);

    Stream OpenRead(string path);

    /// <summary>Creates an empty file that mustn't exist yet.</summary>
    void CreateEmpty(string path);

    /// <summary>Replaces <paramref name="to"/> with <paramref name="from"/> in one step.</summary>
    void Replace(string from, string to);

    /// <summary>Deletes the file; does nothing when it isn't there.</summary>
    void DeleteFile(string path);
}

/// <summary>A server's folders over an open SFTP connection.</summary>
internal sealed class SftpServerFolders(SftpClient client) : IServerFolders
{
    public string ReplaceUnsupported =>
        "This server's SFTP can't replace a file in one step (posix-rename), so ADA can't keep its records "
        + "safe there. Servers running OpenSSH support it.";

    public string Combine(string folder, string relative) => $"{folder.TrimEnd('/')}/{relative}";

    public bool IsDirectory(string path) => Attributes(path)?.IsDirectory == true;

    public long? FileSize(string path) => Attributes(path) is { IsRegularFile: true } a ? a.Size : null;

    public IReadOnlyList<string> Folders(string path) => List(path, directories: true);

    public IReadOnlyList<string> FileNames(string path) => List(path, directories: false);

    public Stream OpenRead(string path) => Wrap(() => (Stream)client.OpenRead(path));

    public void CreateEmpty(string path) => Wrap(() =>
    {
        client.Open(path, FileMode.CreateNew, FileAccess.Write).Dispose();
        return 0;
    });

    public void Replace(string from, string to)
    {
        try
        {
            client.RenameFile(from, to, isPosix: true);
        }
        catch (Exception e) when (e is SshException or NotSupportedException)
        {
            throw new IOException(e.Message, e);
        }
    }

    public void DeleteFile(string path)
    {
        try
        {
            client.DeleteFile(path);
        }
        catch (SftpPathNotFoundException)
        {
            // Not there, which is the goal.
        }
        catch (SshException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    private IReadOnlyList<string> List(string path, bool directories)
    {
        try
        {
            return client.ListDirectory(path)
                .Where(e => e.Name is not ("." or "..") && (directories ? e.IsDirectory : e.IsRegularFile))
                .Select(e => directories ? e.FullName : e.Name)
                .ToList();
        }
        catch (SshException)
        {
            return [];
        }
    }

    private Renci.SshNet.Sftp.SftpFileAttributes? Attributes(string path)
    {
        try
        {
            return client.GetAttributes(path);
        }
        catch (Exception e) when (e is SftpPathNotFoundException or SftpPermissionDeniedException)
        {
            return null;
        }
        catch (SshException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    private static T Wrap<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SshException e)
        {
            throw new IOException(e.Message, e);
        }
    }
}

/// <summary>A server's folders on this PC, including a mounted network share.</summary>
internal sealed class LocalServerFolders : IServerFolders
{
    public string ReplaceUnsupported =>
        "This folder can't replace a file in one step, so ADA can't keep its records safe there. "
        + "If it's a network share, check it's mounted over SMB or NFS with write access.";

    public string Combine(string folder, string relative) =>
        Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));

    public bool IsDirectory(string path) => Directory.Exists(path);

    public long? FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : null;

    public IReadOnlyList<string> Folders(string path) => List(() => Directory.EnumerateDirectories(path).ToList());

    public IReadOnlyList<string> FileNames(string path) =>
        List(() => Directory.EnumerateFiles(path).Select(Path.GetFileName).OfType<string>().ToList());

    public Stream OpenRead(string path) => Wrap(() => File.OpenRead(path));

    public void CreateEmpty(string path) => Wrap(() =>
    {
        new FileStream(path, FileMode.CreateNew, FileAccess.Write).Dispose();
        return 0;
    });

    public void Replace(string from, string to) => Wrap(() =>
    {
        File.Move(from, to, overwrite: true);
        return 0;
    });

    public void DeleteFile(string path) => Wrap(() =>
    {
        File.Delete(path);
        return 0;
    });

    private static IReadOnlyList<string> List(Func<List<string>> list)
    {
        try
        {
            return list();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static T Wrap<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException(e.Message, e);
        }
    }
}

/// <summary>A server's folders over an FTPS session.</summary>
internal sealed class FtpServerFolders(IFtpSession session) : IServerFolders
{
    public string ReplaceUnsupported =>
        "This FTP server can't replace a file in one step, so ADA can't keep its records safe there. "
        + "Use SFTP if the host offers it.";

    public string Combine(string folder, string relative) => $"{folder.TrimEnd('/')}/{relative}";

    public bool IsDirectory(string path) => session.DirectoryExists(path);

    public long? FileSize(string path) => session.Stat(path)?.Size;

    public IReadOnlyList<string> Folders(string path) => List(path).Where(e => e.IsDirectory).Select(e => Combine(path, e.Name)).ToList();

    public IReadOnlyList<string> FileNames(string path) => List(path).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();

    public Stream OpenRead(string path) => FtpGameFiles.Download(session, path);

    public void CreateEmpty(string path)
    {
        if (session.FileExists(path))
        {
            throw new IOException($"{path} already exists.");
        }
        session.Upload(new MemoryStream(), path);
    }

    public void Replace(string from, string to) => session.Rename(from, to);

    public void DeleteFile(string path) => session.DeleteFile(path);

    private IReadOnlyList<FtpEntry> List(string path)
    {
        try
        {
            return session.List(path);
        }
        catch (IOException)
        {
            return [];
        }
    }
}
