using System.Globalization;

namespace ADA.Core.Resolution;

/// <summary>
/// A range over the game's changelist (CL) number, as mods and SML declare compatible game builds:
/// <c>&gt;=513885</c>. Not semver: the game build is a single integer.
/// </summary>
/// <remarks>
/// Every range seen on ficsit.app is <c>&gt;=N</c>. Also accepted, in npm style: <c>&gt;</c>,
/// <c>&lt;=</c>, <c>&lt;</c>, <c>=</c> or a bare number, several comparators separated by spaces
/// (all must hold), alternatives separated by <c>||</c>, and <c>*</c> or empty for any build.
/// </remarks>
public sealed class GameVersionRange
{
    private readonly IReadOnlyList<IReadOnlyList<(string Op, int Value)>> _alternatives;

    public string Text { get; }

    private GameVersionRange(string text, IReadOnlyList<IReadOnlyList<(string Op, int Value)>> alternatives)
    {
        Text = text;
        _alternatives = alternatives;
    }

    /// <summary>
    /// Parses a ficsit.app game range: comparators on the build number, alternatives joined by
    /// <c>||</c>, with <c>*</c> or <c>x</c> matching any build. False for anything else.
    /// </summary>
    public static bool TryParse(string? text, out GameVersionRange range)
    {
        range = null!;
        var trimmed = text?.Trim() ?? "";
        var alternatives = new List<IReadOnlyList<(string, int)>>();

        foreach (var alternative in trimmed.Split("||"))
        {
            var comparators = new List<(string, int)>();
            foreach (var token in alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token is "*" or "x")
                {
                    continue;
                }

                var op = token.StartsWith(">=") || token.StartsWith("<=") ? token[..2]
                    : token.StartsWith('>') || token.StartsWith('<') || token.StartsWith('=') ? token[..1]
                    : "=";
                var digits = op == "=" && !token.StartsWith('=') ? token : token[op.Length..];
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                {
                    return false;
                }

                comparators.Add((op, value));
            }
            alternatives.Add(comparators);
        }

        range = new GameVersionRange(trimmed, alternatives);
        return true;
    }

    /// <summary>True if the game build <paramref name="changelist"/> is in the range.</summary>
    public bool Contains(int changelist) =>
        _alternatives.Any(comparators => comparators.All(c => c.Op switch
        {
            ">=" => changelist >= c.Value,
            ">" => changelist > c.Value,
            "<=" => changelist <= c.Value,
            "<" => changelist < c.Value,
            _ => changelist == c.Value,
        }));

    public override string ToString() => Text;
}
