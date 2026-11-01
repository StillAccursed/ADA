namespace ADA.Infrastructure.Logging;

/// <summary>Keeps log lines readable when they'd otherwise list hundreds of mods.</summary>
internal static class LogText
{
    internal const int SAMPLE_SIZE = 10;

    /// <summary>"A, B, C" for a short list; "A, B, … J and 276 more" past <see cref="SAMPLE_SIZE"/> items.</summary>
    public static string Sample(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= SAMPLE_SIZE
            ? string.Join(", ", list)
            : $"{string.Join(", ", list.Take(SAMPLE_SIZE))} and {list.Count - SAMPLE_SIZE} more";
    }
}
