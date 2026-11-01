using ADA.Core.Settings;
using ADA.Infrastructure.Logging;
using ADA.Tests.Fixtures;
using Microsoft.Extensions.Logging;

namespace ADA.Tests.Logging;

public sealed class LogFilesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string name, params string[] lines) => _dir.WriteFile(name, string.Join('\n', lines) + "\n");

    private static string Line(string level, string message, int second = 0) =>
        $"2026-11-01 16:07:{second:00}.943 +00:00 [{level}] {message}";

    [Fact]
    public async Task ListsDaysNewestFirstWithEachDaysFiles()
    {
        Write("ada-20261031.log", Line("INF", "a"));
        Write("ada-20261101.log", Line("INF", "b"));
        Write("ada-20261101_001.log", Line("INF", "c"));
        Write("ada-notes.log", "x");
        Write("other.txt", "x");

        var days = await new LogFiles(_dir.Path).ListDaysAsync();

        Assert.Equal([new DateOnly(2026, 11, 1), new DateOnly(2026, 10, 31)], days.Select(d => d.Date));
        Assert.Equal(["ada-20261101.log", "ada-20261101_001.log"], days[0].Files.Select(Path.GetFileName));
    }

    [Fact]
    public async Task AMissingFolderHasNoDays()
    {
        Assert.Empty(await new LogFiles(_dir.Combine("none")).ListDaysAsync());
    }

    [Fact]
    public async Task ReadsLevelsAndKeepsStackTracesWithTheirMessage()
    {
        Write("ada-20261101.log",
            Line("INF", "ADA starting", 1),
            Line("WRN", "Couldn't delete archive", 2),
            Line("ERR", "Repair failed", 3),
            "System.IO.IOException: Disk full",
            "   at ADA.Something()",
            Line("FTL", "ADA failed to start", 4));
        var files = new LogFiles(_dir.Path);

        var contents = await files.ReadAsync((await files.ListDaysAsync())[0]);

        Assert.Equal([LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical], contents.Events.Select(e => e.Level));
        var error = contents.Events[2];
        Assert.Equal(("Repair failed", "System.IO.IOException: Disk full\n   at ADA.Something()"), (error.Message, error.More));
        Assert.Equal(string.Join('\n', Line("ERR", "Repair failed", 3), "System.IO.IOException: Disk full", "   at ADA.Something()"), error.Raw);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 16, 7, 1, 943, TimeSpan.Zero), contents.Events[0].At);
        Assert.False(contents.Truncated);
    }

    [Fact]
    public async Task ReadsAFileTheLoggerStillHasOpen()
    {
        var path = _dir.Combine("ada-20261101.log");
        await using var logger = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await logger.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Line("INF", "still writing") + "\n"));
        await logger.FlushAsync();
        var files = new LogFiles(_dir.Path);

        var contents = await files.ReadAsync((await files.ListDaysAsync())[0]);

        Assert.Equal("still writing", Assert.Single(contents.Events).Message);
    }

    [Fact]
    public async Task ReadsOnlyTheEndOfAVeryLargeFileStartingAtAWholeLine()
    {
        Write("ada-20261101.log", [.. Enumerable.Range(0, 50).Select(i => Line("INF", $"message {i}", i))]);
        var files = new LogFiles(_dir.Path, maxBytesPerFile: 200);

        var contents = await files.ReadAsync((await files.ListDaysAsync())[0]);

        Assert.True(contents.Truncated);
        Assert.All(contents.Events, e => Assert.NotNull(e.At));
        Assert.Equal("message 49", contents.Events[^1].Message);
        Assert.InRange(contents.Events.Count, 1, 4);
    }

    private void WriteSized(string name, int bytes, DateTime written)
    {
        var path = _dir.WriteFile(name, new string('x', bytes));
        File.SetLastWriteTimeUtc(path, written);
    }

    [Fact]
    public async Task TrimDeletesTheOldestUntilTheFolderFitsButNeverTheNewest()
    {
        var day = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        WriteSized("ada-20261029.log", 400, day.AddDays(-3));
        WriteSized("ada-20261030.log", 400, day.AddDays(-2));
        WriteSized("ada-20261031.log", 400, day.AddDays(-1));
        WriteSized("ada-20261101.log", 900, day);
        WriteSized("notes.log", 5000, day.AddDays(-9));
        var logs = new LogFiles(_dir.Path);

        Assert.Equal(3, await logs.TrimToAsync(1000));

        Assert.Equal(["ada-20261101.log", "notes.log"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Order());
        Assert.Equal((1, 900L), logs.Measure());
    }

    [Fact]
    public async Task TrimKeepsEverythingThatAlreadyFits()
    {
        WriteSized("ada-20261031.log", 100, DateTime.UtcNow.AddDays(-1));
        WriteSized("ada-20261101.log", 100, DateTime.UtcNow);

        Assert.Equal(0, await new LogFiles(_dir.Path).TrimToAsync(1000));
        Assert.Equal(2, Directory.GetFiles(_dir.Path).Length);
    }

    [Theory]
    [MemberData(nameof(FolderLimits))]
    public void FullFilesFitTheFolderLimitEvenWithTheMessageThatCrossedEach(int megabytes)
    {
        var folder = megabytes * 1024L * 1024;

        Assert.True((LogFiles.FileLimitFor(folder) + LogFiles.MESSAGE_HEADROOM) * LogFiles.FILES_KEPT <= folder);
        Assert.True(LogFiles.FileLimitFor(folder) > 10 * 1024 * 1024);
    }

    public static TheoryData<int> FolderLimits() => [.. AppSettings.LOG_FOLDER_LIMITS];
}
