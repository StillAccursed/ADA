using ADA.Core.Activity;
using ADA.Core.Games;
using ADA.Infrastructure.Activity;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Activity;

public sealed class ActivityLogTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    private string FilePath => _dir.Combine("Activity", "activity.jsonl");

    private ActivityLog Log(int maxEntries = ActivityLog.DEFAULT_MAX_ENTRIES, TimeSpan? maxAge = null) =>
        new(FilePath, time: _time, maxEntries: maxEntries, maxAge: maxAge);

    private ActivityEntry Record(ActivityLog log, string summary) =>
        log.Record(ActivityArea.Mods, ActivityActions.MODS_CHANGED, ActivityOutcome.Succeeded, summary);

    [Fact]
    public async Task ReadsWhatWasRecordedNewestFirst()
    {
        var log = Log();
        Record(log, "first");
        _time.Advance(TimeSpan.FromMinutes(1));
        Record(log, "second");

        var entries = await log.ReadAsync();

        Assert.Equal(["second", "first"], entries.Select(e => e.Summary));
        Assert.Equal(_time.Now.UtcDateTime, entries[0].AtUtc);
    }

    [Fact]
    public async Task KeepsTheInstallDetailsAndError()
    {
        var log = Log();
        var install = new GameInstall("/games/Satisfactory", GameLauncher.Steam, GameHost.CrossOver, GameKind.Client, null, "Steam");

        log.Record(ActivityArea.Backups, ActivityActions.BACKUP_CREATED, ActivityOutcome.Failed, "Couldn't back up",
            install, ["Saves"], "Not enough free space");

        var entry = Assert.Single(await log.ReadAsync());
        Assert.Equal(ActivityArea.Backups, entry.Area);
        Assert.Equal(ActivityOutcome.Failed, entry.Outcome);
        Assert.Equal("Satisfactory (Steam)", entry.Install);
        Assert.Equal("/games/Satisfactory", entry.InstallKey);
        Assert.Equal(["Saves"], entry.Details);
        Assert.Equal("Not enough free space", entry.Error);
    }

    [Fact]
    public async Task AMissingFileHasNoEntries()
    {
        Assert.Empty(await Log().ReadAsync());
    }

    [Fact]
    public async Task ATornLastLineIsSkippedAndTheNextEntryStillReads()
    {
        var log = Log();
        Record(log, "before the crash");
        File.AppendAllText(FilePath, "{\"id\":\"half-writ");

        Record(log, "after the crash");

        Assert.Equal(["after the crash", "before the crash"], (await log.ReadAsync()).Select(e => e.Summary));
    }

    [Fact]
    public async Task UnknownActionsReadButUnknownAreasAreSkipped()
    {
        var log = Log();
        Record(log, "known");
        File.AppendAllText(FilePath,
            """{"id":"00000000-0000-0000-0000-000000000001","atUtc":"2026-11-01T12:00:00Z","area":"Mods","action":"FromTheFuture","outcome":"Succeeded","summary":"newer action","details":[]}"""
            + "\n"
            + """{"id":"00000000-0000-0000-0000-000000000002","atUtc":"2026-11-01T12:00:00Z","area":"Weather","action":"X","outcome":"Succeeded","summary":"newer area","details":[]}"""
            + "\n");

        var summaries = (await log.ReadAsync()).Select(e => e.Summary).ToList();

        Assert.Contains("newer action", summaries);
        Assert.DoesNotContain("newer area", summaries);
    }

    [Fact]
    public void RecordingNeverThrows()
    {
        File.WriteAllText(_dir.Combine("Activity"), "a file where the folder should be");

        var entry = Record(Log(), "can't be written");

        Assert.Equal("can't be written", entry.Summary);
    }

    [Fact]
    public async Task RecordedIsRaisedWithTheEntry()
    {
        var log = Log();
        ActivityEntry? raised = null;
        log.Recorded += (_, e) => raised = e;

        var entry = Record(log, "something");

        Assert.Equal(entry, raised);
        Assert.Single(await log.ReadAsync());
    }

    [Fact]
    public async Task ManyThreadsRecordingAtOnceLoseNothing()
    {
        var log = Log();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => Record(log, $"entry {i}"))));

        Assert.Equal(200, (await log.ReadAsync()).Count);
    }

    [Fact]
    public async Task TrimDropsOldEntriesThenTheOldestBeyondTheLimit()
    {
        var log = Log(maxEntries: 3, maxAge: TimeSpan.FromDays(30));
        Record(log, "too old");
        _time.Advance(TimeSpan.FromDays(31));
        for (var i = 1; i <= 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            Record(log, $"recent {i}");
        }

        await log.TrimAsync();

        Assert.Equal(["recent 4", "recent 3", "recent 2"], (await log.ReadAsync()).Select(e => e.Summary));
        Assert.Equal(3, File.ReadAllLines(FilePath).Length);
    }

    [Fact]
    public async Task TrimRemovesUnreadableLines()
    {
        var log = Log();
        Record(log, "good");
        File.AppendAllText(FilePath, "not json\n");

        await log.TrimAsync();

        Assert.Single(File.ReadAllLines(FilePath));
    }

    [Fact]
    public async Task ClearRemovesEverything()
    {
        var log = Log();
        var cleared = false;
        log.Cleared += (_, _) => cleared = true;
        Record(log, "something");

        await log.ClearAsync();

        Assert.Empty(await log.ReadAsync());
        Assert.True(cleared);
    }
}
