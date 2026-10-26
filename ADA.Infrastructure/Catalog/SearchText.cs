using System.Text.RegularExpressions;

namespace ADA.Infrastructure.Catalog;

/// <summary>Turns mod descriptions (Markdown with some HTML) into text worth indexing.</summary>
internal static partial class SearchText
{
    /// <summary>
    /// Text that describes images in a description: Markdown alt text, HTML <c>alt</c>/<c>title</c>
    /// attributes, and image file names (<c>conveyor-belt-mk6.png</c> → "conveyor belt mk6"). This is
    /// what makes images searchable without analysing the pictures themselves.
    /// </summary>
    public static string ImageText(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return "";

        var parts = new List<string>();
        foreach (Match m in MarkdownImage().Matches(markdown))
        {
            parts.Add(m.Groups["alt"].Value);
            parts.Add(FileNameWords(m.Groups["url"].Value));
        }
        foreach (Match m in HtmlImage().Matches(markdown))
        {
            foreach (Match attr in HtmlTextAttribute().Matches(m.Value))
                parts.Add(attr.Groups["value"].Value);
            if (HtmlSrc().Match(m.Value) is { Success: true } src)
                parts.Add(FileNameWords(src.Groups["url"].Value));
        }
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    /// <summary>Description with images, links' URLs, HTML tags and Markdown punctuation removed.</summary>
    public static string Plain(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return "";

        var text = MarkdownImage().Replace(markdown, " ");
        text = MarkdownLink().Replace(text, "${text}");
        text = HtmlTag().Replace(text, " ");
        text = MarkdownPunctuation().Replace(text, " ");
        return Whitespace().Replace(text, " ").Trim();
    }

    private static string FileNameWords(string url)
    {
        var path = url.Split('?', '#')[0];
        var name = Path.GetFileNameWithoutExtension(path);
        return NonWord().Replace(name, " ").Trim();
    }

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<url>[^)\s]*)[^)]*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\[(?<text>[^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlImage();

    [GeneratedRegex(@"\b(?:alt|title)\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTextAttribute();

    [GeneratedRegex(@"\bsrc\s*=\s*[""'](?<url>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlSrc();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"[#*_`>~|\-]{1,}")]
    private static partial Regex MarkdownPunctuation();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
