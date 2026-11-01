using ADA.Core.Activity;
using ADA.Infrastructure.Activity;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Activity;

public sealed class ActivityRecordingTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ActivityLog _log;

    public ActivityRecordingTests()
    {
        _log = new ActivityLog(_dir.Combine("activity.jsonl"));
    }

    public void Dispose() => _dir.Dispose();

    private Task<int> Run(Func<Task<int>> work, Func<int, ActivityDescription?>? describe = null,
        Func<Exception, bool>? isNotAFailure = null) =>
        _log.RecordAsync(ActivityArea.Mods, ActivityActions.MODS_CHANGED, null, work,
            describe ?? (n => new ActivityDescription($"Did {n}", ["detail"])), "Thing failed", ["tried"], isNotAFailure);

    [Fact]
    public async Task RecordsASuccessWithItsDescription()
    {
        Assert.Equal(3, await Run(() => Task.FromResult(3)));

        var entry = Assert.Single(await _log.ReadAsync());
        Assert.Equal((ActivityOutcome.Succeeded, "Did 3"), (entry.Outcome, entry.Summary));
        Assert.Equal(["detail"], entry.Details);
    }

    [Fact]
    public async Task ANullDescriptionRecordsNothing()
    {
        await Run(() => Task.FromResult(1), _ => null);

        Assert.Empty(await _log.ReadAsync());
    }

    [Fact]
    public async Task RecordsAFailureThenRethrowsIt()
    {
        await Assert.ThrowsAsync<IOException>(() => Run(() => throw new IOException("Disk full")));

        var entry = Assert.Single(await _log.ReadAsync());
        Assert.Equal((ActivityOutcome.Failed, "Thing failed", "Disk full"), (entry.Outcome, entry.Summary, entry.Error));
        Assert.Equal(["tried"], entry.Details);
    }

    [Fact]
    public async Task RecordsACancellation()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run(() => throw new OperationCanceledException()));

        var entry = Assert.Single(await _log.ReadAsync());
        Assert.Equal((ActivityOutcome.Cancelled, "Thing cancelled"), (entry.Outcome, entry.Summary));
    }

    [Fact]
    public async Task ExceptionsThatArentFailuresAreRethrownUnrecorded()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run(() => throw new InvalidOperationException(), isNotAFailure: e => e is InvalidOperationException));

        Assert.Empty(await _log.ReadAsync());
    }

    [Fact]
    public async Task WithoutALogItOnlyRunsTheWork()
    {
        ActivityLog? none = null;

        var result = await none.RecordAsync(ActivityArea.Mods, ActivityActions.MODS_CHANGED, null,
            () => Task.FromResult(7), _ => new ActivityDescription("x"), "x failed");

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task ApostrophesAreWrittenAsTheyAre()
    {
        _log.Record(ActivityArea.Backups, ActivityActions.BACKUP_CREATED, ActivityOutcome.Failed, "Couldn't back up");

        Assert.Contains("Couldn't back up", await File.ReadAllTextAsync(_dir.Combine("activity.jsonl")));
    }
}
