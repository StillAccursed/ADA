namespace ADA.Tests.Fixtures;

/// <summary>A clock tests move by hand; <see cref="GetUtcNow"/> and, when given, the local time zone are faked.</summary>
/// <param name="zone">The zone local times are in; the machine's by default.</param>
public sealed class ManualTimeProvider(DateTimeOffset start, TimeZoneInfo? zone = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override TimeZoneInfo LocalTimeZone => zone ?? base.LocalTimeZone;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
