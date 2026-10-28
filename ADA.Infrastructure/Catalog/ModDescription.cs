using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ADA.Infrastructure.Catalog;

/// <summary>Prepares catalogue Markdown and HTML for formatted descriptions, plain text and the gallery.</summary>
public static partial class ModDescription
{
    /// <summary>
    /// Keeps Markdown, structural HTML and HTTPS images for native rendering. Removes CSS, scripts,
    /// comments and author attributes. Image references are resolved to inline images.
    /// Fenced and inline code are preserved verbatim, including markup used in examples.
    /// </summary>
    public static string Formatted(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        // Definitions and image references inside code or hidden blocks are not document content.
        var markup = ContentRegion().Replace(text, "\n");
        var labels = ReferenceImage().Matches(markup)
            .Select(match => ReferenceLabel(match.Groups["label"].Value is { Length: > 0 } label
                ? label : match.Groups["alt"].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imageReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match definition in ReferenceDefinition().Matches(markup))
        {
            var label = ReferenceLabel(definition.Groups["label"].Value);
            if (labels.Contains(label))
            {
                imageReferences.TryAdd(label, definition.Groups["url"].Value);
            }
        }

        var result = new StringBuilder(text.Length);
        var position = 0;
        foreach (Match region in ContentRegion().Matches(text))
        {
            result.Append(FormatMarkup(text[position..region.Index], imageReferences));
            if (region.Groups["code"].Success)
            {
                result.Append(region.Value);
            }

            position = region.Index + region.Length;
        }

        result.Append(FormatMarkup(text[position..], imageReferences));
        var formatted = result.ToString().Trim();
        return HtmlImageUrl().IsMatch(formatted) || !string.IsNullOrWhiteSpace(HtmlTag().Replace(formatted, ""))
            ? formatted : "";
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
    internal static IReadOnlyList<string> ImageUrls(string? markdown, int max = 30) =>
        string.IsNullOrEmpty(markdown) ? [] : ImageUrlsInFormatted(Formatted(markdown), max);

    /// <summary>
    /// <see cref="ImageUrls"/> for text already returned by <see cref="Formatted"/>, so a caller that
    /// shows the description doesn't format it twice. Images inside code blocks are not counted.
    /// </summary>
    public static IReadOnlyList<string> ImageUrlsInFormatted(string formatted, int max = 30)
    {
        var text = ContentRegion().Replace(formatted, "\n");
        return HtmlImageUrl().Matches(text)
            .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value))
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

    /// <summary>
    /// Keeps only semantic tags and HTTPS images, stripping author styling and image dimensions.
    /// Converts supported images to a common HTML form so Markdown and HTML share loading and layout.
    /// </summary>
    private static string FormatMarkup(string text, IReadOnlyDictionary<string, string> imageReferences)
    {
        text = ReferenceDefinition().Replace(text, match =>
            imageReferences.ContainsKey(ReferenceLabel(match.Groups["label"].Value)) ? "" : match.Value);
        text = MarkdownImage().Replace(text, match => FormatImage(match.Groups["url"].Value, match.Groups["alt"].Value));
        text = ReferenceImage().Replace(text, match =>
        {
            var label = ReferenceLabel(match.Groups["label"].Value is { Length: > 0 } reference
                ? reference : match.Groups["alt"].Value);
            return imageReferences.TryGetValue(label, out var url) ? FormatImage(url, match.Groups["alt"].Value) : "";
        });
        return DisplayTag().Replace(text, match =>
        {
            var tag = match.Groups["tag"].Value.ToLowerInvariant();
            if (tag == "img" && !match.Groups["closing"].Success)
            {
                return FormatImage(WebUtility.HtmlDecode(ImageAttribute(match.Value, "src")),
                    WebUtility.HtmlDecode(ImageAttribute(match.Value, "alt")));
            }

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

    /// <summary>Encodes the only two permitted image attributes; unsupported locations produce no image.</summary>
    private static string FormatImage(string url, string alt)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? $"<img src=\"{WebUtility.HtmlEncode(uri.AbsoluteUri)}\" alt=\"{WebUtility.HtmlEncode(alt)}\">"
            : "";
    }

    private static string ImageAttribute(string tag, string name)
    {
        return HtmlAttribute().Matches(tag)
            .FirstOrDefault(match => match.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))?
            .Groups["value"].Value ?? "";
    }

    private static string ReferenceLabel(string label) => ReferenceLabelSpace().Replace(label.Trim(), " ");

    /// <summary>
    /// Matches non-content blocks and code together so backticks inside author CSS or JavaScript
    /// cannot split a style/script block and leak its remaining content into the description.
    /// </summary>
    [GeneratedRegex("""(?<code>^[ ]{0,3}(?<fence>`{3,}|~{3,})[^\n]*\n.*?(?:^[ ]{0,3}\k<fence>[`~]*[ \t]*(?:\n|\z)|\z)|(?<!`)(?<ticks>`+)(?!`).*?(?<!`)\k<ticks>(?!`))|<(?<tag>style|script|head|iframe|object)\b[^>]*>.*?(?:</\k<tag>\s*>|\z)|<!--.*?(?:-->|\z)""", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline, 1000)]
    private static partial Regex ContentRegion();

    [GeneratedRegex("""<\s*(?<closing>/)?\s*(?<tag>[a-z][a-z0-9:-]*)(?:"[^"]*"|'[^']*'|[^'">])*?>""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DisplayTag();

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\](?!\()(?:[ \t]*\[(?<label>[^\]]*)\])?")]
    private static partial Regex ReferenceImage();

    [GeneratedRegex("""^[ ]{0,3}\[(?<label>[^\]]+)\]:[ \t]*(?:<(?<url>[^>\r\n]+)>|(?<url>\S+))[^\r\n]*$""", RegexOptions.Multiline, 1000)]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"\s+")]
    private static partial Regex ReferenceLabelSpace();

    [GeneratedRegex("""(?<name>[^\s=/>]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s>]+))""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HtmlAttribute();

    [GeneratedRegex(@"<img src=""(?<url>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlImageUrl();

    [GeneratedRegex("""!\[(?<alt>[^\]]*)\]\([ \t]*(?:<(?<url>[^>\r\n]+)>|(?<url>(?:[^()\s]|\((?<depth>)|\)(?<-depth>))+)(?(depth)(?!)))[ \t]*(?:(?:"[^"]*"|'[^']*'|\([^)]*\))[ \t]*)?\)""", RegexOptions.None, 1000)]
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
