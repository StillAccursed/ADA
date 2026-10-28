using ADA.Infrastructure.Catalog;

namespace ADA.Tests.Catalog;

public class ModDescriptionTests
{
    [Fact]
    public void Formatted_keeps_markdown_and_structural_html_without_author_css()
    {
        const string MARKDOWN = """
            # Features

            **Rain**, *snow* and `weather_enabled`.

            - Fog
            - Storms

            | Feature | Enabled |
            | --- | --- |
            | Fog | Yes |

            <div class="features" style="display: grid"><h2>Weather</h2><p>Storms &amp; wind</p></div>
            """;

        var formatted = ModDescription.Formatted(MARKDOWN);

        Assert.Contains("# Features", formatted);
        Assert.Contains("**Rain**, *snow* and `weather_enabled`.", formatted);
        Assert.Contains("- Fog\n- Storms", formatted);
        Assert.Contains("| Fog | Yes |", formatted);
        Assert.Contains("<div><h2>Weather</h2><p>Storms &amp; wind</p></div>", formatted);
        Assert.DoesNotContain("display:", formatted);
        Assert.DoesNotContain("class=", formatted);
    }

    [Fact]
    public void Formatted_removes_hidden_content_but_keeps_https_images_in_place()
    {
        const string MARKDOWN = """
            <div><STYLE>.rp-title { display: inline-block; }</STYLE></div>
            <!-- implementation notes -->
            <script>window.alert('example');</script>
            # Refined Power
            <section><b>Multiplayer Compatible</b><div>Everyone!</div></section>
            ![Banner](https://example.com/banner.png)
            <img src="https://example.com/photo.png" alt="Photo">
            ![Reference image][photo]
            """;

        var formatted = ModDescription.Formatted(MARKDOWN);

        Assert.Contains("# Refined Power", formatted);
        Assert.Contains("<b>Multiplayer Compatible</b><div>Everyone!</div>", formatted);
        Assert.DoesNotContain("rp-title", formatted);
        Assert.DoesNotContain("implementation notes", formatted);
        Assert.DoesNotContain("window.alert", formatted);
        Assert.Contains("<img src=\"https://example.com/banner.png\" alt=\"Banner\">", formatted);
        Assert.Contains("<img src=\"https://example.com/photo.png\" alt=\"Photo\">", formatted);
        Assert.DoesNotContain("![Reference image]", formatted);
        Assert.Equal(["https://example.com/banner.png", "https://example.com/photo.png"], ModDescription.ImageUrls(MARKDOWN));
    }

    [Theory]
    [InlineData("```html\n<style>display: grid;</style>\n<!-- example -->\n<img src=\"example.png\">\n```")]
    [InlineData("~~~html\n<script>example()</script>\n~~~")]
    [InlineData("Use `<style>display: grid;</style>` in your example.")]
    [InlineData("Use ``<img src=\"image.png\">`` in your example.")]
    public void Formatted_preserves_markup_inside_code_examples(string markdown)
    {
        Assert.Equal(markdown, ModDescription.Formatted(markdown));
    }

    [Theory]
    [InlineData("<style>display: grid;")]
    [InlineData("<!-- unfinished comment")]
    [InlineData("<script>unfinished script")]
    public void Formatted_removes_unclosed_non_content_blocks(string markup)
    {
        Assert.Equal("Visible", ModDescription.Formatted("Visible\n" + markup));
    }

    [Fact]
    public void Readable_does_not_leak_css_or_comments_into_changelogs()
    {
        Assert.Equal("Weather\n\nMore rain.", ModDescription.Readable(
            "<style>.weather { display: flex; }</style><!-- hidden --># Weather\n\nMore **rain**."));
    }

    [Fact]
    public void Backticks_inside_scripts_and_styles_do_not_leak_hidden_content()
    {
        Assert.Equal("Visible", ModDescription.Formatted(
            "<style>.code::before { content: '`example`'; }</style>Visible<script>const text = `hidden`;</script>"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("<div><style>display: grid;</style><img src=\"image.png\"></div>")]
    public void Descriptions_without_visible_content_fall_back_to_the_short_description(string? markdown)
    {
        var mod = new ADA.ViewModels.ModItemViewModel(
            ADA.Tests.Fixtures.CatalogFixtures.Mod("Example", shortDescription: "Short description", fullDescription: markdown));

        Assert.Equal("Short description", mod.Description);
    }

    [Fact]
    public void Keeps_paragraphs_and_lists_but_drops_markup_images_and_urls()
    {
        const string MARKDOWN = """
            # Weather Extended

            Adds **rain** and *snow*. See [the wiki](https://example.com/wiki).

            ![Rain](https://example.com/rain.png)
            <img src="x.png" alt="x"><br>
            - Fog
            * Storms &amp; wind
            """;

        Assert.Equal("Weather Extended\n\nAdds rain and snow. See the wiki.\n\n• Fog\n• Storms & wind",
            ModDescription.Readable(MARKDOWN));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Empty_descriptions_are_empty(string? markdown) => Assert.Equal("", ModDescription.Readable(markdown));

    [Fact]
    public void Gallery_takes_https_images_in_order_without_repeats()
    {
        const string MARKDOWN = """
            ![one](https://example.com/1.png "title")
            <img alt="two" src="https://example.com/2.jpg">
            ![insecure](http://example.com/3.png)
            ![again](https://example.com/1.png)
            [not an image](https://example.com/page)
            """;

        Assert.Equal(["https://example.com/1.png", "https://example.com/2.jpg"], ModDescription.ImageUrls(MARKDOWN));
    }

    [Fact]
    public void Mixed_images_keep_their_positions_between_paragraphs()
    {
        const string MARKDOWN = "Before\n\n![Banner](https://example.com/banner.png)\n\nBetween\n\n<img src='https://example.com/photo.png'>\n\nAfter";

        Assert.Equal("Before\n\n<img src=\"https://example.com/banner.png\" alt=\"Banner\">\n\nBetween\n\n<img src=\"https://example.com/photo.png\" alt=\"\">\n\nAfter",
            ModDescription.Formatted(MARKDOWN));
    }

    [Theory]
    [InlineData("![Banner](https://example.com/banner.png)")]
    [InlineData("<div><img src='https://example.com/banner.png'></div>")]
    public void Image_only_descriptions_do_not_fall_back_to_short_text(string markdown)
    {
        var mod = new ADA.ViewModels.ModItemViewModel(
            ADA.Tests.Fixtures.CatalogFixtures.Mod("Example", shortDescription: "Fallback", fullDescription: markdown));

        Assert.Contains("<img src=\"https://example.com/banner.png\"", mod.Description);
        Assert.DoesNotContain("Fallback", mod.Description);
        Assert.Equal("", ModDescription.Readable(markdown));
    }

    [Theory]
    [InlineData("http://example.com/image.png")]
    [InlineData("file:///tmp/image.png")]
    [InlineData("avares://ADA/Assets/image.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("javascript:alert(1)")]
    [InlineData("../image.png")]
    public void Unsupported_image_locations_are_removed(string url)
    {
        Assert.Equal("Before\n\nAfter", ModDescription.Formatted($"Before\n![Image]({url})\nAfter"));
        Assert.Equal("", ModDescription.Formatted($"<img src='{url}'>"));
        Assert.Empty(ModDescription.ImageUrls($"![Image]({url})\n<img src='{url}'>"));
    }

    [Fact]
    public void Html_images_keep_only_encoded_source_and_alt_attributes()
    {
        const string MARKDOWN = "<IMG data-src='https://wrong.example/image.png' SRC='https://example.com/image.png?a=1&amp;b=2' alt='A &quot;photo&quot; &amp; more' width='9000' height='10' style='display:none' onerror='alert(1)' />";

        Assert.Equal("<img src=\"https://example.com/image.png?a=1&amp;b=2\" alt=\"A &quot;photo&quot; &amp; more\">",
            ModDescription.Formatted(MARKDOWN));
        Assert.Equal(["https://example.com/image.png?a=1&b=2"], ModDescription.ImageUrls(MARKDOWN));
    }

    [Theory]
    [InlineData("![Image](https://example.com/image_(1).png \"Title\")", "https://example.com/image_(1).png")]
    [InlineData("![Image](<https://example.com/image.png> 'Title')", "https://example.com/image.png")]
    [InlineData("![Image](https://example.com/image.png?x=1&y=2)", "https://example.com/image.png?x=1&amp;y=2")]
    public void Markdown_image_destinations_are_preserved_and_encoded(string markdown, string expected)
    {
        Assert.Equal($"<img src=\"{expected}\" alt=\"Image\">", ModDescription.Formatted(markdown));
    }

    [Fact]
    public void Reference_images_resolve_case_and_whitespace_and_stay_in_gallery_order()
    {
        const string MARKDOWN = """
            ![First][ Banner  Image ]
            <img src="https://example.com/second.png">
            ![Third][]
            ![Fourth]

            [banner image]: <https://example.com/first.png> "Title"
            [Third]: https://example.com/third.png
            [Fourth]: https://example.com/fourth.png
            """;

        var formatted = ModDescription.Formatted(MARKDOWN);

        Assert.Contains("<img src=\"https://example.com/first.png\" alt=\"First\">", formatted);
        Assert.Contains("<img src=\"https://example.com/third.png\" alt=\"Third\">", formatted);
        Assert.Contains("<img src=\"https://example.com/fourth.png\" alt=\"Fourth\">", formatted);
        Assert.DoesNotContain("[banner image]:", formatted);
        Assert.DoesNotContain("![", formatted);
        Assert.Equal(["https://example.com/first.png", "https://example.com/second.png", "https://example.com/third.png", "https://example.com/fourth.png"],
            ModDescription.ImageUrls(MARKDOWN));
        Assert.Equal("", ModDescription.Readable(MARKDOWN));
    }

    [Fact]
    public void Code_and_hidden_images_do_not_enter_the_gallery_or_define_visible_references()
    {
        const string MARKDOWN = """
            <!-- <img src="https://example.com/hidden.png"> -->
            `<img src="https://example.com/inline-example.png">`
            ```markdown
            ![Example](https://example.com/code.png)
            [hidden]: https://example.com/definition.png
            ```
            ![Hidden][hidden]
            ![Visible](https://example.com/visible.png)
            """;

        Assert.Equal(["https://example.com/visible.png"], ModDescription.ImageUrls(MARKDOWN));
        Assert.DoesNotContain("![Hidden]", ModDescription.Formatted(MARKDOWN));
    }
}
