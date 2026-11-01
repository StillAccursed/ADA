using ADA.Core.Activity;
using ADA.Core.Games;
using ADA.Infrastructure.Activity;
using ADA.Platform.Detection;
using ADA.Tests.Fixtures;
using ADA.ViewModels;

namespace ADA.Tests.ViewModels;

/// <summary>The Dashboard's game watcher records each time the game is played.</summary>
public sealed class DashboardActivityTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ActivityLog _activity;
    private volatile bool _running;

    public DashboardActivityTests()
    {
        _activity = new ActivityLog(_dir.Combine("activity.jsonl"));
    }

    public void Dispose() => _dir.Dispose();

    private DashboardViewModel Dashboard() => DashboardFactory.Create(_dir, new NoGames(), _ => throw new InvalidOperationException(),
        findRunning: _ => Task.FromResult<IReadOnlyList<GameProcess>>(_running ? [new GameProcess(1, "FactoryGameSteam")] : []),
        watchInterval: TimeSpan.FromMilliseconds(20), activity: _activity);

    /// <summary>Watches until the game has been seen running and then closed, then stops watching.</summary>
    private async Task PlayAsync(DashboardViewModel dashboard)
    {
        using var stop = new CancellationTokenSource();
        var watching = dashboard.WatchGameAsync(stop.Token);
        _running = true;
        await Wait.Until(() => dashboard.IsGameRunning);
        _running = false;
        await Wait.Until(() => !dashboard.IsGameRunning);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);
    }

    [Fact]
    public async Task RecordsHowLongTheGameWasPlayed()
    {
        await PlayAsync(Dashboard());

        var entry = Assert.Single(await _activity.ReadAsync());
        Assert.Equal((ActivityArea.Game, ActivityActions.GAME_PLAYED, "Played Satisfactory for 0 m"),
            (entry.Area, entry.Action, entry.Summary));
        Assert.Null(entry.InstallKey);
    }

    [Fact]
    public async Task AGameAlreadyRunningWhenADAStartedWasPlayedForAtLeastThatLong()
    {
        _running = true;

        await PlayAsync(Dashboard());

        Assert.Equal("Played Satisfactory for at least 0 m", Assert.Single(await _activity.ReadAsync()).Summary);
    }

    private sealed class NoGames : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => [];
    }
}
