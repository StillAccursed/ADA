using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ADA.Infrastructure.Logging;

/// <summary>One day of ADA's log.</summary>
/// <param name="Files">Oldest first; more than one only when two ADAs logged that day (the second writes <c>_001</c>).</param>
/// <param name="Size">All of its files, in bytes.</param>
public sealed record LogDay(DateOnly Date, IReadOnlyList<string> Files, long Size);

/// <summary>One logged message: its first line, any lines after it (an exception's stack trace), and the text as written.</summary>
/// <param name="At">Null for text before the first timestamped line, e.g. where a large file's start was skipped.</param>
/// <param name="More">The lines after the first; null when there are none.</param>
/// <param name="Raw">The message exactly as written, every line, for copying.</param>
public sealed record LogEvent(DateTimeOffset? At, LogLevel Level, string Message, string? More, string Raw);

/// <param name="Truncated">A file was too large, and only its end was read.</param>
public sealed record LogDayContents(IReadOnlyList<LogEvent> Events, bool Truncated);

/// <summary>
/// Reads ADA's own Serilog files for the Logs tab: <c>ada-yyyyMMdd.log</c> in the logs folder, written as
/// <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message}</c> with any exception on the lines after.
/// Files are opened sharing read and write, so the file the logger is still writing can be read.
/// </summary>
/// <param name="maxBytesPerFile">Only the last this many bytes of a larger file are read.</param>
public sealed partial class LogFiles(string folder, long maxBytesPerFile = LogFiles.DEFAULT_MAX_BYTES_PER_FILE)
{
    public const long DEFAULT_MAX_BYTES_PER_FILE = 20L * 1024 * 1024;

    /// <summary>How many log files are kept; the logger's count limit, and each file's share of the folder limit.</summary>
    public const int FILES_KEPT = 7;

    /// <summary>
    /// Room left in each file's share for the message that crosses its limit: Serilog writes that one before it stops.
    /// Far larger than any message ADA logs, stack traces included.
    /// </summary>
    public const long MESSAGE_HEADROOM = 1024 * 1024;

    public string Folder => folder;

    /// <summary>Every day with a log file, newest first; none when the folder doesn't exist yet.</summary>
    public Task<IReadOnlyList<LogDay>> ListDaysAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (!Directory.Exists(folder))
        {
            return (IReadOnlyList<LogDay>)[];
        }
        return Directory.GetFiles(folder, "ada-*.log")
            .Select(file => (File: file, Date: DateOf(file)))
            .Where(f => f.Date is not null)
            .GroupBy(f => f.Date!.Value)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var files = g.Select(f => f.File).Order(StringComparer.Ordinal).ToList();
                return new LogDay(g.Key, files, files.Sum(f => new FileInfo(f).Length));
            })
            .ToList();
    }, ct);

    /// <summary>
    /// The size limit for one log file under a folder limit: an even share, less <see cref="MESSAGE_HEADROOM"/>, so
    /// <see cref="FILES_KEPT"/> files fit within it even when each ends with the message that crossed its limit.
    /// </summary>
    public static long FileLimitFor(long folderLimitBytes) => folderLimitBytes / FILES_KEPT - MESSAGE_HEADROOM;

    /// <summary>How many log files there are, and their total size; (0, 0) when the folder doesn't exist.</summary>
    public (int Count, long Bytes) Measure()
    {
        var files = LogFilesNewestFirst();
        return (files.Count, files.Sum(f => f.Length));
    }

    /// <summary>
    /// Deletes the oldest log files until the folder holds at most <paramref name="maxBytes"/>. The newest file is never
    /// deleted, since the logger may be writing it. A file that can't be deleted (e.g. open in an editor) is skipped
    /// and left for the next trim; the method doesn't throw for it.
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    public Task<int> TrimToAsync(long maxBytes, CancellationToken ct = default) => Task.Run(() =>
    {
        var files = LogFilesNewestFirst();
        var total = files.Sum(f => f.Length);
        var deleted = 0;
        foreach (var file in files.Skip(1).Reverse())
        {
            if (total <= maxBytes)
            {
                break;
            }
            ct.ThrowIfCancellationRequested();
            try
            {
                var size = file.Length;
                file.Delete();
                total -= size;
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left for the next trim.
            }
        }
        return deleted;
    }, ct);

    /// <summary>A day's messages, oldest first. A file larger than the limit is read from the first whole line of its last part.</summary>
    /// <exception cref="IOException">A file couldn't be read, e.g. it was deleted as the logger rotated it.</exception>
    public Task<LogDayContents> ReadAsync(LogDay day, CancellationToken ct = default) => Task.Run(() =>
    {
        var events = new List<LogEvent>();
        var truncated = false;
        foreach (var file in day.Files)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skipPartialLine = stream.Length > maxBytesPerFile;
            if (skipPartialLine)
            {
                stream.Seek(-maxBytesPerFile, SeekOrigin.End);
                truncated = true;
            }
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (skipPartialLine)
            {
                reader.ReadLine();
            }
            Parse(reader, events, ct);
        }
        return new LogDayContents(events, truncated);
    }, ct);

    /// <summary>
    /// ADA's log files (any day, including <c>_001</c> siblings), newest first by last write: a second ADA's file shares
    /// a date with the first's, and the one still being written is the one to keep.
    /// </summary>
    private List<FileInfo> LogFilesNewestFirst() => Directory.Exists(folder)
        ? new DirectoryInfo(folder).GetFiles("ada-*.log")
            .Where(f => DateOf(f.Name) is not null)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList()
        : [];

    /// <summary>The day in a log file's name, or null for a file that isn't one of ADA's daily logs.</summary>
    private static DateOnly? DateOf(string file) =>
        FileName().Match(Path.GetFileName(file)) is { Success: true } match
        && DateOnly.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>
    /// Adds each message to <paramref name="events"/>: a timestamped line starts one, and the lines after it, up to
    /// the next timestamped line, belong to it. Blank lines at a message's end are dropped.
    /// </summary>
    private static void Parse(TextReader reader, List<LogEvent> events, CancellationToken ct)
    {
        DateTimeOffset? at = null;
        var level = LogLevel.Information;
        string? message = null;
        var more = new List<string>();

        void Flush()
        {
            if (message is null)
            {
                return;
            }
            while (more.Count > 0 && more[^1].Length == 0)
            {
                more.RemoveAt(more.Count - 1);
            }
            var stamp = at is { } time ? $"{time:yyyy-MM-dd HH:mm:ss.fff zzz} [{LevelCode(level)}] " : "";
            var raw = string.Join('\n', [stamp + message, .. more]);
            events.Add(new LogEvent(at, level, message, more.Count == 0 ? null : string.Join('\n', more), raw));
            more.Clear();
        }

        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (Line().Match(line) is { Success: true } match)
            {
                Flush();
                at = DateTimeOffset.ParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
                level = LevelOf(match.Groups[2].Value);
                message = match.Groups[3].Value;
            }
            else if (message is null)
            {
                // Text before any timestamp: kept as a message of its own rather than lost.
                at = null;
                level = LogLevel.Information;
                message = line;
            }
            else
            {
                more.Add(line);
            }
        }
        Flush();
    }

    private static LogLevel LevelOf(string code) => code switch
    {
        "VRB" => LogLevel.Trace,
        "DBG" => LogLevel.Debug,
        "WRN" => LogLevel.Warning,
        "ERR" => LogLevel.Error,
        "FTL" => LogLevel.Critical,
        _ => LogLevel.Information,
    };

    private static string LevelCode(LogLevel level) => level switch
    {
        LogLevel.Trace => "VRB",
        LogLevel.Debug => "DBG",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "FTL",
        _ => "INF",
    };

    [GeneratedRegex(@"^ada-(\d{8})(?:_\d+)?\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex FileName();

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) \[([A-Z]{3})\] (.*)$")]
    private static partial Regex Line();
}
