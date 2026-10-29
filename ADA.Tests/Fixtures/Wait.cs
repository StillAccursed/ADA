namespace ADA.Tests.Fixtures;

/// <summary>Waits for state that view models and connections update a moment after the event, e.g. through a captured context.</summary>
internal static class Wait
{
    /// <summary>Polls every 10 ms for up to 3 seconds, then asserts the condition.</summary>
    public static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}
