using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ADA.Infrastructure.Servers;

/// <summary>Who holds a server's lock: written into the lock file so others can say who.</summary>
public sealed record LockHolder(string Machine, int Process, DateTimeOffset Since)
{
    /// <summary>A lock whose holder couldn't be read, e.g. one left by a crash before it was written.</summary>
    public static readonly LockHolder UNKNOWN = new("", 0, DateTimeOffset.MinValue);

    [JsonIgnore]
    public bool IsUnknown => Machine.Length == 0;
}

/// <summary>The few file operations a lock file needs, on an SFTP server or in a shared folder.</summary>
/// <remarks>Failures are <see cref="IOException"/>, whatever the transport.</remarks>
internal interface ILockFile
{
    /// <summary>The same for every handle to this lock in this process, whichever connection made it.</summary>
    string Key { get; }

    /// <summary>Creates the lock file for writing.</summary>
    /// <exception cref="IOException">It already exists, or can't be created.</exception>
    Stream CreateNew();

    /// <summary>The lock file's contents, or null when there isn't one.</summary>
    Stream? OpenRead();

    void Delete();
}

/// <summary>
/// A server install's lock. Neither SFTP nor a network share can be relied on for file locking, so
/// the lock is a file created with "must not exist" and deleted on release. If ADA crashes, the
/// file stays behind. A lock left by this PC whose process has exited is taken over automatically;
/// one from another PC needs the user to confirm (Servers page).
/// </summary>
internal sealed class RemoteLock : IDisposable
{
    // Locks this process holds right now, by server and path. A lock file naming this process but
    // not listed here was left by a release that failed, e.g. when the connection dropped.
    private static readonly ConcurrentDictionary<string, byte> HELD_HERE = new(StringComparer.Ordinal);

    private readonly ILockFile _file;
    private readonly ILogger _log;

    private RemoteLock(ILockFile file, ILogger log)
    {
        _file = file;
        _log = log;
    }

    /// <exception cref="LockHeldException">Another ADA, or another operation in this one, holds it.</exception>
    public static RemoteLock Acquire(ILockFile file, ILogger log)
    {
        if (!HELD_HERE.TryAdd(file.Key, 0))
        {
            throw new LockHeldException("This ADA is already changing this server's mods.");
        }
        try
        {
            var me = new LockHolder(Environment.MachineName, Environment.ProcessId, DateTimeOffset.UtcNow);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var stream = file.CreateNew();
                    JsonSerializer.Serialize(stream, me, LockJsonContext.Default.LockHolder);
                    return new RemoteLock(file, log);
                }
                catch (IOException) when (attempt == 0 && Holder(file) is { } holder && IsAbandonedByThisPc(holder))
                {
                    log.LogWarning("Taking over a server lock left by this PC's ADA process {Process}", holder.Process);
                    file.Delete();
                }
                catch (IOException) when (Holder(file) is { } holder)
                {
                    throw new LockHeldException(holder.IsUnknown
                        ? "This server's mods were left mid-change by an ADA that didn't finish. If nothing is changing them now, take over from the Servers page."
                        : $"{holder.Machine} has been changing this server's mods since {holder.Since.ToLocalTime():t}. If it has stopped, take over from the Servers page.");
                }
            }
            throw new LockHeldException("Another ADA is changing this server's mods.");
        }
        catch
        {
            HELD_HERE.TryRemove(file.Key, out _);
            throw;
        }
    }

    /// <summary>Removes a lock another PC left, once the user has confirmed that PC is no longer using it.</summary>
    /// <exception cref="IOException">It couldn't be removed.</exception>
    public static void Break(ILockFile file) => file.Delete();

    public void Dispose()
    {
        try
        {
            _file.Delete();
        }
        catch (IOException e)
        {
            // Left behind for the next operation from this process to take over (IsAbandonedByThisPc).
            _log.LogWarning(e, "Couldn't release the server lock {Lock}", _file.Key);
        }
        finally
        {
            HELD_HERE.TryRemove(_file.Key, out _);
        }
    }

    /// <summary>Who holds the lock, unless it's this process; null when it's free, unreadable or ours.</summary>
    internal static LockHolder? HeldByOther(ILockFile file) =>
        Holder(file) is { } holder
        && !(holder.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && holder.Process == Environment.ProcessId)
            ? holder
            : null;

    /// <returns>
    /// Null when there's no lock; <see cref="LockHolder.UNKNOWN"/> when there is one whose holder
    /// can't be read (empty, not JSON, or unreadable), so it's still treated as held.
    /// </returns>
    private static LockHolder? Holder(ILockFile file)
    {
        try
        {
            using var stream = file.OpenRead();
            if (stream is null)
            {
                return null;
            }
            return JsonSerializer.Deserialize(stream, LockJsonContext.Default.LockHolder) is { Machine.Length: > 0 } holder
                ? holder
                : LockHolder.UNKNOWN;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return LockHolder.UNKNOWN;
        }
    }

    /// <summary>
    /// This PC wrote it, and either that process has exited, or it's this process. Only reached when
    /// this process isn't holding the lock (<see cref="HELD_HERE"/>), so the file is from a release
    /// that failed, e.g. when the connection dropped.
    /// </summary>
    private static bool IsAbandonedByThisPc(LockHolder holder)
    {
        // Who left an unreadable lock can't be told, so the user decides (Take over).
        if (holder.IsUnknown || !holder.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (holder.Process == Environment.ProcessId)
        {
            return true;
        }
        try
        {
            using var process = Process.GetProcessById(holder.Process);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}

/// <summary>A lock file on an SFTP server.</summary>
internal sealed class SftpLockFile(SftpClient client, string path) : ILockFile
{
    public string Key { get; } = $"{client.ConnectionInfo.Username}@{client.ConnectionInfo.Host}:{client.ConnectionInfo.Port}{path}";

    public Stream CreateNew() => Wrap(() => (Stream)client.Open(path, FileMode.CreateNew, FileAccess.Write));

    public Stream? OpenRead()
    {
        try
        {
            return client.OpenRead(path);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
        catch (SshException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public void Delete() => Wrap(() =>
    {
        client.DeleteFile(path);
        return 0;
    });

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

/// <summary>
/// A lock file in a folder this PC opens, possibly on a network share. "Create only if it doesn't
/// exist" is honoured across PCs by SMB and NFS, unlike <c>FileShare.None</c> on macOS and Linux.
/// </summary>
internal sealed class FolderLockFile(string path) : ILockFile
{
    public string Key { get; } = "folder:" + Path.GetFullPath(path);

    public Stream CreateNew() => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    public Stream? OpenRead()
    {
        try
        {
            return File.OpenRead(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException(e.Message, e);
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LockHolder))]
internal sealed partial class LockJsonContext : JsonSerializerContext;
