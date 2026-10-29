using ADA.Infrastructure.Servers;

namespace ADA.Tests.Fixtures;

/// <summary>
/// An FTP server in memory, behaving like a Unix one (vsftpd, ProFTPD): making a folder that exists
/// fails, and a rename replaces an existing file unless <see cref="RenameReplaces"/> is off. Times
/// are kept to the second, as <c>MDTM</c> reports them.
/// </summary>
internal sealed class InMemoryFtpSession : IFtpSession
{
    private readonly Dictionary<string, (byte[] Data, DateTime Modified)> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal) { "/" };

    public string Key => "test@ftp.example:21";

    /// <summary>Off for servers (e.g. IIS) that refuse to rename onto an existing file.</summary>
    public bool RenameReplaces { get; set; } = true;

    /// <summary>Uploads to a path ending with this fail, e.g. to break a lock's holder file.</summary>
    public string? FailUploadsEnding { get; set; }

    /// <summary>Every file downloaded, in order, to show what a check reads.</summary>
    public List<string> Downloads { get; } = [];

    /// <summary>The connection has dropped: every call fails.</summary>
    public bool Dropped { get; set; }

    public bool FileExists(string path) => Check(() => _files.ContainsKey(path));

    public bool DirectoryExists(string path) => Check(() => _folders.Contains(path));

    public void CreateDirectories(string path) => Check(() =>
    {
        var current = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (_files.ContainsKey(current))
            {
                throw new IOException($"{current} is a file.");
            }
            _folders.Add(current);
        }
        return true;
    });

    public void MakeDirectory(string path) => Check(() =>
    {
        if (_folders.Contains(path) || _files.ContainsKey(path) || !_folders.Contains(Parent(path)))
        {
            throw new IOException($"550 Can't create {path}.");
        }
        return _folders.Add(path);
    });

    public IReadOnlyList<FtpEntry> List(string path) => Check(() =>
    {
        var prefix = path.TrimEnd('/') + "/";
        var folders = _folders.Where(f => Parent(f) == path.TrimEnd('/') && f != "/")
            .Select(f => new FtpEntry(Name(f), f, true, 0));
        var files = _files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && !f.Key[prefix.Length..].Contains('/'))
            .Select(f => new FtpEntry(Name(f.Key), f.Key, false, f.Value.Data.Length));
        return (IReadOnlyList<FtpEntry>)folders.Concat(files).ToList();
    });

    public FtpFileStat? Stat(string path) => Check(() =>
        _files.TryGetValue(path, out var file) ? new FtpFileStat(file.Data.Length, file.Modified) : null);

    public void Download(string path, Stream into) => Check(() =>
    {
        if (!_files.TryGetValue(path, out var file))
        {
            throw new IOException($"550 {path}: no such file.");
        }
        into.Write(file.Data);
        Downloads.Add(path);
        return true;
    });

    public void Upload(Stream from, string path) => Check(() =>
    {
        if (!_folders.Contains(Parent(path)) || (FailUploadsEnding is { } ending && path.EndsWith(ending, StringComparison.Ordinal)))
        {
            throw new IOException($"553 Can't write {path}.");
        }
        using var copy = new MemoryStream();
        from.CopyTo(copy);
        var now = DateTime.UtcNow;
        _files[path] = (copy.ToArray(), new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc));
        return true;
    });

    public void Rename(string from, string to) => Check(() =>
    {
        if (_files.Remove(from, out var file))
        {
            if (_folders.Contains(to) || (_files.ContainsKey(to) && !RenameReplaces))
            {
                _files[from] = file;
                throw new IOException($"553 {to} exists.");
            }
            _files[to] = file;
            return true;
        }
        if (!_folders.Contains(from) || _folders.Contains(to) || _files.ContainsKey(to))
        {
            throw new IOException($"550 Can't rename {from}.");
        }
        foreach (var folder in _folders.Where(f => f == from || f.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
        {
            _folders.Remove(folder);
            _folders.Add(to + folder[from.Length..]);
        }
        foreach (var (path, data) in _files.Where(f => f.Key.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
        {
            _files.Remove(path);
            _files[to + path[from.Length..]] = data;
        }
        return true;
    });

    public void DeleteFile(string path) => Check(() => _files.Remove(path));

    public void DeleteDirectory(string path) => Check(() =>
    {
        _folders.RemoveWhere(f => f == path || f.StartsWith(path + "/", StringComparison.Ordinal));
        foreach (var file in _files.Keys.Where(f => f.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            _files.Remove(file);
        }
        return true;
    });

    public void Noop() => Check(() => true);

    /// <summary>Puts a file in place directly, for laying out a server.</summary>
    public void Put(string path, string content)
    {
        CreateDirectories(Parent(path));
        _files[path] = (System.Text.Encoding.UTF8.GetBytes(content), DateTime.UtcNow);
    }

    public string? Read(string path) => _files.TryGetValue(path, out var file) ? System.Text.Encoding.UTF8.GetString(file.Data) : null;

    private static string Parent(string path) => path.LastIndexOf('/') is var i and > 0 ? path[..i] : "/";

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private T Check<T>(Func<T> action) => Dropped ? throw new IOException("421 Connection closed.") : action();
}
