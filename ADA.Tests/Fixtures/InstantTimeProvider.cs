namespace ADA.Tests.Fixtures;

/// <summary>
/// Real time, except that every timer fires at once: retry backoffs don't sleep, and each requested
/// wait is recorded so tests can check the backoff schedule.
/// </summary>
public sealed class InstantTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> _delays = [];

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_delays)
                return _delays.ToList();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_delays)
                _delays.Add(dueTime);
            dueTime = TimeSpan.Zero;
        }
        return base.CreateTimer(callback, state, dueTime, period);
    }
}
