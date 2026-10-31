namespace ADA.Tests.Fixtures;

/// <summary>A clock tests move by hand; only <see cref="GetUtcNow"/> is faked.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
