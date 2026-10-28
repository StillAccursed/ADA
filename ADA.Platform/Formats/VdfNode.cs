using System.Text;

namespace ADA.Platform.Formats;

/// <summary>
/// Minimal reader for Valve's text KeyValues format, used by Steam's <c>libraryfolders.vdf</c> and
/// <c>appmanifest_*.acf</c> files. A node is either a string value or a block of child nodes.
/// </summary>
internal sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode> _children = new(StringComparer.OrdinalIgnoreCase);

    public string? Value { get; }

    public IReadOnlyDictionary<string, VdfNode> Children => _children;

    public VdfNode? this[string key] => _children.GetValueOrDefault(key);

    private VdfNode(string? value) => Value = value;

    public string? GetString(string key) => this[key]?.Value;

    /// <summary>Parses a Valve KeyValues document.</summary>
    /// <exception cref="FormatException">The text is malformed.</exception>
    public static VdfNode Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new VdfNode(null);
        ReadBlock(root, tokens, isRoot: true);
        return root;
    }

    /// <summary>Null when the file is missing, unreadable or malformed.</summary>
    public static VdfNode? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static void ReadBlock(VdfNode parent, IEnumerator<Token> tokens, bool isRoot)
    {
        while (tokens.MoveNext())
        {
            var token = tokens.Current;
            if (token.Kind == TokenKind.Close)
            {
                if (isRoot)
                {
                    throw new FormatException("Unexpected '}' at top level.");
                }

                return;
            }

            if (token.Kind != TokenKind.String)
            {
                throw new FormatException("Expected a key.");
            }

            if (!tokens.MoveNext())
            {
                throw new FormatException($"Key '{token.Text}' has no value.");
            }

            var next = tokens.Current;
            if (next.Kind == TokenKind.String)
            {
                parent._children[token.Text] = new VdfNode(next.Text);
            }
            else if (next.Kind == TokenKind.Open)
            {
                var child = new VdfNode(null);
                ReadBlock(child, tokens, isRoot: false);
                parent._children[token.Text] = child;
            }
            else
            {
                throw new FormatException($"Key '{token.Text}' is followed by '}}'.");
            }
        }

        if (!isRoot)
        {
            throw new FormatException("Unclosed '{'.");
        }
    }

    private enum TokenKind { String, Open, Close }

    private readonly record struct Token(TokenKind Kind, string Text);

    private static IEnumerable<Token> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '{')
            {
                i++;
                yield return new Token(TokenKind.Open, "{");
            }
            else if (c == '}')
            {
                i++;
                yield return new Token(TokenKind.Close, "}");
            }
            else if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', var other => other });
                    }
                    else
                    {
                        sb.Append(text[i]);
                    }
                    i++;
                }
                if (i >= text.Length)
                {
                    throw new FormatException("Unterminated string.");
                }

                i++;
                yield return new Token(TokenKind.String, sb.ToString());
            }
            else
            {
                // Unquoted token: KeyValues allows bare words up to whitespace or a brace.
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
                {
                    i++;
                }

                yield return new Token(TokenKind.String, text[start..i]);
            }
        }
    }
}
