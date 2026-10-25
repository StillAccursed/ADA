using System.Text.RegularExpressions;

namespace Overlord.Infrastructure.Catalog;

/// <summary>Turns a mod's description (Markdown with some HTML) into text to read in the details pane.</summary>
public static partial class ModDescription
{
    /// <summary>
    /// Keeps paragraphs and list items, drops images and markup, and keeps link text without the
    /// URL. Nothing in a description becomes clickable, since descriptions are written by anyone.
    /// </summary>
    public static string Readable(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var text = markdown.Replace("\r\n", "\n");
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
            return [];
        return MarkdownImageUrl().Matches(markdown).Select(m => (m.Index, Url: m.Groups["url"].Value))
            .Concat(HtmlImageUrl().Matches(markdown).Select(m => (m.Index, Url: m.Groups["url"].Value)))
            .OrderBy(m => m.Index)
            .Select(m => m.Url)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }

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
