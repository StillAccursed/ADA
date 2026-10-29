using System.Net.Sockets;
using FluentFTP;
using FluentFTP.Exceptions;

namespace ADA.Infrastructure.Servers;

/// <summary>One entry of an FTP folder listing. Listings don't carry times: LIST's are in the server's local time and coarse.</summary>
internal sealed record FtpEntry(string Name, string FullName, bool IsDirectory, long Size);

/// <summary>A file's size and its modification time from <c>MDTM</c>, which is UTC to the second.</summary>
internal sealed record FtpFileStat(long Size, DateTime ModifiedUtc);

/// <summary>
/// The FTP operations ADA uses, with every failure as <see cref="IOException"/>. Calls are serialised,
/// because one FTP connection carries one command at a time; transfers go through whole streams, so
/// no data connection stays open between calls. Tests use an in-memory version.
/// </summary>
internal interface IFtpSession
{
    /// <summary>Identifies the server and login, for lock keys: <c>user@host:port</c>.</summary>
    string Key { get; }

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Creates the folder and any missing parents; nothing when it exists.</summary>
    void CreateDirectories(string path);

    /// <summary>Creates one folder, failing if it already exists. FTP's only "create if missing" step, used for the lock.</summary>
    /// <exception cref="IOException">It exists, or couldn't be created.</exception>
    void MakeDirectory(string path);

    /// <summary>The files and folders directly inside <paramref name="path"/>.</summary>
    IReadOnlyList<FtpEntry> List(string path);

    /// <summary>A file's size and time; null when there's no file there.</summary>
    FtpFileStat? Stat(string path);

    void Download(string path, Stream into);

    /// <summary>Writes <paramref name="from"/> as the file at <paramref name="path"/>, replacing any.</summary>
    void Upload(Stream from, string path);

    /// <summary>
    /// Renames with <c>RNFR</c>/<c>RNTO</c>, onto an existing name when the server allows, which Unix
    /// FTP servers do in one step. FluentFTP's own rename refuses an existing target, so it isn't used.
    /// </summary>
    void Rename(string from, string to);

    /// <summary>Deletes the file; nothing when it isn't there.</summary>
    void DeleteFile(string path);

    /// <summary>Deletes the folder and everything in it; nothing when it isn't there.</summary>
    void DeleteDirectory(string path);

    /// <summary>Checks the connection still answers.</summary>
    void Noop();
}

/// <summary>An <see cref="IFtpSession"/> over a connected FluentFTP client, which it owns.</summary>
internal sealed class FluentFtpSession(FtpClient client) : IFtpSession, IDisposable
{
    private readonly Lock _gate = new();

    public string Key { get; } = $"{client.Credentials?.UserName}@{client.Host}:{client.Port}";

    public bool FileExists(string path) => Run(() => client.FileExists(path));

    public bool DirectoryExists(string path) => Run(() => client.DirectoryExists(path));

    public void CreateDirectories(string path) => Do(() => client.CreateDirectory(path, force: true));

    public void MakeDirectory(string path) => Do(() => Expect(client.Execute($"MKD {path}")));

    public IReadOnlyList<FtpEntry> List(string path) => Run(() => client.GetListing(path)
        .Where(i => i.Type is FtpObjectType.File or FtpObjectType.Directory && i.Name is not ("." or ".."))
        .Select(i => new FtpEntry(i.Name, i.FullName, i.Type == FtpObjectType.Directory, i.Size))
        .ToList());

    public FtpFileStat? Stat(string path) => Run(() =>
    {
        var size = client.GetFileSize(path, -1);
        // MDTM is defined as UTC (RFC 3659), so only the kind needs setting.
        return size < 0 ? null : new FtpFileStat(size, DateTime.SpecifyKind(client.GetModifiedTime(path), DateTimeKind.Utc));
    });

    public void Download(string path, Stream into) => Do(() =>
    {
        if (!client.DownloadStream(into, path))
        {
            throw new IOException($"Couldn't download {path}.");
        }
        return true;
    });

    public void Upload(Stream from, string path) => Do(() =>
    {
        if (client.UploadStream(from, path, FtpRemoteExists.Overwrite, createRemoteDir: false) != FtpStatus.Success)
        {
            throw new IOException($"Couldn't upload {path}.");
        }
        return true;
    });

    public void Rename(string from, string to) => Do(() =>
    {
        Expect(client.Execute($"RNFR {from}"));
        Expect(client.Execute($"RNTO {to}"));
        return true;
    });

    public void DeleteFile(string path) => Do(() =>
    {
        if (client.FileExists(path))
        {
            client.DeleteFile(path);
        }
        return true;
    });

    public void DeleteDirectory(string path) => Do(() =>
    {
        if (client.DirectoryExists(path))
        {
            client.DeleteDirectory(path);
        }
        return true;
    });

    public void Noop() => Do(() => Expect(client.Execute("NOOP")));

    /// <summary>
    /// Checks the connection still answers, without waiting: when a transfer holds the connection,
    /// that's proof enough it's alive. False when the server didn't answer or the session is closed.
    /// </summary>
    public bool TryNoop()
    {
        if (!_gate.TryEnter())
        {
            return true;
        }
        try
        {
            return client.Execute("NOOP").Success;
        }
        catch (Exception e) when (e is FtpException or IOException or SocketException or TimeoutException
                                      or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _gate.Exit();
        }
    }

    public void Dispose() => client.Dispose();

    private static bool Expect(FtpReply reply) =>
        reply.Success ? true : throw new IOException($"The server refused {reply.Command}: {reply.ErrorMessage}");

    private T Run<T>(Func<T> action)
    {
        lock (_gate)
        {
            try
            {
                return action();
            }
            catch (Exception e) when (e is FtpException or SocketException or TimeoutException or InvalidOperationException)
            {
                throw new IOException(e.Message, e);
            }
        }
    }

    /// <summary><see cref="Run{T}"/> for a call whose result isn't needed.</summary>
    private void Do<T>(Func<T> action) => Run(action);
}
