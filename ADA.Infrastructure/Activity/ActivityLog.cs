using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADA.Core.Activity;
using ADA.Core.Games;
using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Activity;

/// <summary>
/// What ADA did, kept as one JSON line per entry in <c>ADA/Activity/activity.jsonl</c>, oldest first. Recording appends
/// a line under a lock and never throws, so it can't fail the operation it describes. Lines that can't be read (a
/// crash mid-append, or an entry this ADA can't understand) are skipped.
/// </summary>
/// <param name="maxEntries">The most entries <see cref="TrimAsync"/> keeps.</param>
/// <param name="maxAge">The oldest entries <see cref="TrimAsync"/> keeps.</param>
public sealed class ActivityLog(
    string path,
    ILogger<ActivityLog>? log = null,
    TimeProvider? time = null,
    int maxEntries = ActivityLog.DEFAULT_MAX_ENTRIES,
    TimeSpan? maxAge = null)
{
    public const int DEFAULT_MAX_ENTRIES = 10_000;

    private static readonly TimeSpan DEFAULT_MAX_AGE = TimeSpan.FromDays(365);

    private static readonly byte[] NEWLINE = "\n"u8.ToArray();

    // Relaxed escaping keeps apostrophes and accented letters readable in the file. It's only read back by ADA,
    // never embedded in HTML, so the stricter default buys nothing here.
    private static readonly ActivityJsonContext JSON = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    });

    private readonly ILogger _log = log ?? NullLogger<ActivityLog>.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _maxAge = maxAge ?? DEFAULT_MAX_AGE;
    private readonly Lock _gate = new();

    // Set by ReadLocked under the lock: whether the last read skipped any line, so a trim rewrites the file without them.
    private bool _hadUnreadable;

    /// <summary>Raised on the recording thread after an entry is written, outside the lock.</summary>
    public event EventHandler<ActivityEntry>? Recorded;

    /// <summary>Raised on the clearing thread after <see cref="ClearAsync"/>, outside the lock.</summary>
    public event EventHandler? Cleared;

    /// <summary>The folder holding the history file.</summary>
    public string Folder => Path.GetDirectoryName(Path.GetFullPath(path))!;

    /// <summary>
    /// Records an entry stamped with the current time and returns it. Never throws: a failed write is logged and the
    /// entry is returned anyway, so the operation it describes carries on.
    /// </summary>
    /// <param name="install">The install it was about; null for activity not about one install.</param>
    public ActivityEntry Record(ActivityArea area, string action, ActivityOutcome outcome, string summary,
        GameInstall? install = null, IReadOnlyList<string>? details = null, string? error = null)
    {
        var entry = new ActivityEntry(Guid.NewGuid(), _time.GetUtcNow().UtcDateTime, area, action, outcome, summary,
            install?.DisplayName, install?.GamePath, details ?? [], error);
        try
        {
            var line = JsonSerializer.SerializeToUtf8Bytes(entry, JSON.ActivityEntry);
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                EndTornLine(stream);
                stream.Seek(0, SeekOrigin.End);
                stream.Write(line);
                stream.Write(NEWLINE);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Couldn't record activity: {Summary}", summary);
            return entry;
        }

        Recorded?.Invoke(this, entry);
        return entry;
    }

    /// <summary>Every readable entry, newest first; entries with the same time come latest-recorded first. A missing file has none.</summary>
    public Task<IReadOnlyList<ActivityEntry>> ReadAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        lock (_gate)
        {
            // Reversed first: the sort is stable, so entries with equal times keep the reversed (latest-first) file order.
            var entries = ReadLocked(ct);
            entries.Reverse();
            return (IReadOnlyList<ActivityEntry>)entries.OrderByDescending(e => e.AtUtc).ToList();
        }
    }, ct);

    /// <summary>
    /// Drops entries older than the age limit, then the oldest beyond the count limit, rewriting the file in one step.
    /// Unreadable lines are dropped too. Called once at startup.
    /// </summary>
    public Task TrimAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(path))
                {
                    return;
                }
                var all = ReadLocked(ct);
                var cutoff = _time.GetUtcNow().UtcDateTime - _maxAge;
                var kept = all.Where(e => e.AtUtc >= cutoff).OrderBy(e => e.AtUtc).TakeLast(maxEntries).ToList();
                if (kept.Count == all.Count && !_hadUnreadable)
                {
                    return;
                }
                AtomicFile.Write(path, stream =>
                {
                    foreach (var entry in kept)
                    {
                        stream.Write(JsonSerializer.SerializeToUtf8Bytes(entry, JSON.ActivityEntry));
                        stream.Write(NEWLINE);
                    }
                });
                _log.LogInformation("Trimmed activity from {Before} to {After} entries", all.Count, kept.Count);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Couldn't trim the activity history");
        }
    }, ct);

    /// <summary>Deletes every entry.</summary>
    /// <exception cref="IOException">The file couldn't be deleted.</exception>
    public Task ClearAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            File.Delete(path);
        }
        _log.LogInformation("Cleared the activity history");
        Cleared?.Invoke(this, EventArgs.Empty);
    });

    private List<ActivityEntry> ReadLocked(CancellationToken ct)
    {
        var entries = new List<ActivityEntry>();
        _hadUnreadable = false;
        if (!File.Exists(path))
        {
            return entries;
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize(line, JSON.ActivityEntry) is { } entry)
                {
                    entries.Add(entry);
                    continue;
                }
            }
            catch (JsonException)
            {
                // A line torn by a crash, or an entry from a newer ADA this one can't read.
            }
            _hadUnreadable = true;
        }
        if (_hadUnreadable)
        {
            _log.LogWarning("Skipped unreadable lines in the activity history");
        }
        return entries;
    }

    /// <summary>
    /// Ends a line a crash left unfinished, so the next entry starts on a line of its own instead of being glued to it.
    /// </summary>
    private static void EndTornLine(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return;
        }
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() != '\n')
        {
            stream.Seek(0, SeekOrigin.End);
            stream.Write(NEWLINE);
        }
    }
}

[JsonSerializable(typeof(ActivityEntry))]
internal sealed partial class ActivityJsonContext : JsonSerializerContext;
