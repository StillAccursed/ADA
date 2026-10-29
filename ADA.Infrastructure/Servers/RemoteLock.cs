using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ADA.Infrastructure.Servers;

/// <summary>Who holds a server's lock: written into the lock file so others can say who.</summary>
internal sealed record LockHolder(string Machine, int Process, DateTimeOffset Since);

/// <summary>
/// A server install's lock. SFTP has no file locking, so the lock is a file created with
/// "must not exist" and deleted on release. If ADA crashes, the file stays behind. A lock left
/// by this PC whose process has exited is taken over automatically; one from another PC needs the
/// user to confirm (Servers page, step 5).
/// </summary>
internal sealed class RemoteLock : IDisposable
{
    private readonly SftpClient _client;
    private readonly string _path;
    private readonly ILogger _log;

    private RemoteLock(SftpClient client, string path, ILogger log)
    {
        _client = client;
        _path = path;
        _log = log;
    }

    /// <exception cref="LockHeldException">Another ADA holds it.</exception>
    public static RemoteLock Acquire(SftpClient client, string path, ILogger log)
    {
        var me = new LockHolder(Environment.MachineName, Environment.ProcessId, DateTimeOffset.UtcNow);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var stream = client.Open(path, FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(stream, me, LockJsonContext.Default.LockHolder);
                return new RemoteLock(client, path, log);
            }
            catch (SshException) when (attempt == 0 && Holder(client, path) is { } holder && IsAbandonedByThisPc(holder))
            {
                log.LogWarning("Taking over a server lock left by this PC's ADA process {Process}", holder.Process);
                client.DeleteFile(path);
            }
            catch (SshException) when (Holder(client, path) is { } holder)
            {
                throw new LockHeldException(
                    $"{holder.Machine} has been changing this server's mods since {holder.Since.ToLocalTime():t}. If it has stopped, take over from the Servers page.");
            }
        }
        throw new LockHeldException("Another ADA is changing this server's mods.");
    }

    /// <summary>Removes a lock another PC left, once the user has confirmed that PC is no longer using it.</summary>
    public static void Break(SftpClient client, string path) => client.DeleteFile(path);

    public void Dispose()
    {
        try
        {
            _client.DeleteFile(_path);
        }
        catch (Exception e) when (e is SshException or IOException)
        {
            // The next operation from this process takes it over (IsAbandonedByThisPc).
            _log.LogWarning(e, "Couldn't release the server lock {Lock}", _path);
        }
    }

    /// <returns>Null when the lock file is missing or unreadable.</returns>
    private static LockHolder? Holder(SftpClient client, string path)
    {
        try
        {
            using var stream = client.OpenRead(path);
            return JsonSerializer.Deserialize(stream, LockJsonContext.Default.LockHolder);
        }
        catch (Exception e) when (e is SshException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// This PC wrote it, and either that process has exited, or it's this process (a release that
    /// failed when the connection dropped). This process runs one operation per install at a time.
    /// </summary>
    private static bool IsAbandonedByThisPc(LockHolder holder)
    {
        if (!holder.Machine.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
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

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LockHolder))]
internal sealed partial class LockJsonContext : JsonSerializerContext;
