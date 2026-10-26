using System.Text;
using System.Text.RegularExpressions;

namespace ADA.Infrastructure.Catalog;

/// <summary>Prepares catalogue Markdown and HTML for formatted descriptions, plain text and the gallery.</summary>
public static partial class ModDescription
{
    /// <summary>
    /// Keeps Markdown and structural HTML for native rendering. Removes CSS, scripts, comments,
    /// images and HTML attributes; images remain available through <see cref="ImageUrls"/>.
    /// Fenced and inline code are preserved verbatim, including markup used in examples.
    /// </summary>
    public static string Formatted(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new StringBuilder(text.Length);
        var position = 0;
        foreach (Match region in ContentRegion().Matches(text))
        {
            result.Append(FormatMarkup(text[position..region.Index]));
            if (region.Groups["code"].Success)
            {
                result.Append(region.Value);
            }

            position = region.Index + region.Length;
        }

        result.Append(FormatMarkup(text[position..]));
        var formatted = result.ToString().Trim();
        return string.IsNullOrWhiteSpace(HtmlTag().Replace(formatted, "")) ? "" : formatted;
    }

    /// <summary>
    /// Keeps paragraphs and list items, drops images and markup, and keeps link text without the
    /// URL. Nothing in a description becomes clickable, since descriptions are written by anyone.
    /// </summary>
    public static string Readable(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var text = Formatted(markdown);
        text = MarkdownImage().Replace(text, "");
        text = MarkdownLink().Replace(text, "${text}");
        text = LineBreakTag().Replace(text, "\n");
        text = HtmlTag().Replace(text, "");
        text = Heading().Replace(text, "");
        text = ListBullet().Replace(text, "• ");
        text = Emphasis().Replace(text, "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = TrailingSpace().Replace(text, "\n");
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    /// <summary>The https images a description shows (Markdown and HTML), in order, for the mod page's gallery.</summary>
    public static IReadOnlyList<string> ImageUrls(string? markdown, int max = 30)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return [];
        }
        return MarkdownImageUrl().Matches(markdown).Select(m => (m.Index, Url: m.Groups["url"].Value))
            .Concat(HtmlImageUrl().Matches(markdown).Select(m => (m.Index, Url: m.Groups["url"].Value)))
            .OrderBy(m => m.Index)
            .Select(m => m.Url)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Removes images and author attributes before passing semantic tags to the HTML plugin.
    /// Unwraps unsupported elements so their visible content remains readable in the app's theme.
    /// </summary>
    private static string FormatMarkup(string text)
    {
        text = MarkdownImage().Replace(text, "");
        text = ReferenceImage().Replace(text, "");
        return DisplayTag().Replace(text, match =>
        {
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            return tag switch
            {
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "p" or "div" or "span"
                    or "strong" or "b" or "em" or "i" or "u" or "s" or "del" or "br" or "hr"
                    or "ul" or "ol" or "li" or "blockquote" or "pre" or "code" or "table"
                    or "thead" or "tbody" or "tfoot" or "tr" or "th" or "td"
                    => $"<{match.Groups["closing"].Value}{tag}>",
                "center" or "section" or "article" => $"<{match.Groups["closing"].Value}div>",
                _ => "",
            };
        });
    }

    /// <summary>
    /// Matches non-content blocks and code together so backticks inside author CSS or JavaScript
    /// cannot split a style/script block and leak its remaining content into the description.
    /// </summary>
    [GeneratedRegex("""(?<code>^[ ]{0,3}(?<fence>`{3,}|~{3,})[^\n]*\n.*?(?:^[ ]{0,3}\k<fence>[`~]*[ \t]*(?:\n|\z)|\z)|(?<!`)(?<ticks>`+)(?!`).*?(?<!`)\k<ticks>(?!`))|<(?<tag>style|script|head|iframe|object)\b[^>]*>.*?(?:</\k<tag>\s*>|\z)|<!--.*?(?:-->|\z)""", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline, 1000)]
    private static partial Regex ContentRegion();

    [GeneratedRegex("""<\s*(?<closing>/)?\s*(?<tag>[a-z][a-z0-9:-]*)(?:"[^"]*"|'[^']*'|[^'">])*?>""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DisplayTag();

    [GeneratedRegex(@"!\[[^\]]*\](?:[ \t]*\[[^\]]*\])?")]
    private static partial Regex ReferenceImage();

    [GeneratedRegex(@"!\[[^\]]*\]\((?<url>[^)\s]+)[^)]*\)")]
    private static partial Regex MarkdownImageUrl();

    [GeneratedRegex(@"<img\b[^>]*?\bsrc\s*=\s*[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlImageUrl();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<\s*(?:br|/p|/li|/h[1-6]|/div)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}[ \t]*", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[ \t]*[-*+][ \t]+", RegexOptions.Multiline)]
    private static partial Regex ListBullet();

    [GeneratedRegex(@"\*\*|__|(?<![\w*])\*(?=\S)|(?<=\S)\*(?![\w*])|`")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
