using Overlord.Infrastructure.Catalog;

namespace Overlord.Tests.Catalog;

public class ModDescriptionTests
{
    [Fact]
    public void Formatted_keeps_markdown_and_structural_html_without_author_css()
    {
        const string markdown = """
            # Features

            **Rain**, *snow* and `weather_enabled`.

            - Fog
            - Storms

            | Feature | Enabled |
            | --- | --- |
            | Fog | Yes |

            <div class="features" style="display: grid"><h2>Weather</h2><p>Storms &amp; wind</p></div>
            """;

        var formatted = ModDescription.Formatted(markdown);

        Assert.Contains("# Features", formatted);
        Assert.Contains("**Rain**, *snow* and `weather_enabled`.", formatted);
        Assert.Contains("- Fog\n- Storms", formatted);
        Assert.Contains("| Fog | Yes |", formatted);
        Assert.Contains("<div><h2>Weather</h2><p>Storms &amp; wind</p></div>", formatted);
        Assert.DoesNotContain("display:", formatted);
        Assert.DoesNotContain("class=", formatted);
    }

    [Fact]
    public void Formatted_removes_style_script_comments_and_images_but_keeps_visible_content()
    {
        const string markdown = """
            <div><STYLE>.rp-title { display: inline-block; }</STYLE></div>
            <!-- implementation notes -->
            <script>window.alert('example');</script>
            # Refined Power
            <section><b>Multiplayer Compatible</b><div>Everyone!</div></section>
            ![Banner](https://example.com/banner.png)
            <img src="https://example.com/photo.png" alt="Photo">
            ![Reference image][photo]
            """;

        var formatted = ModDescription.Formatted(markdown);

        Assert.Contains("# Refined Power", formatted);
        Assert.Contains("<b>Multiplayer Compatible</b><div>Everyone!</div>", formatted);
        Assert.DoesNotContain("rp-title", formatted);
        Assert.DoesNotContain("implementation notes", formatted);
        Assert.DoesNotContain("window.alert", formatted);
        Assert.DoesNotContain("![", formatted);
        Assert.DoesNotContain("<img", formatted);
        Assert.Equal(["https://example.com/banner.png", "https://example.com/photo.png"], ModDescription.ImageUrls(markdown));
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
        var mod = new Overlord.ViewModels.ModItemViewModel(
            Overlord.Tests.Fixtures.CatalogFixtures.Mod("Example", shortDescription: "Short description", fullDescription: markdown));

        Assert.Equal("Short description", mod.Description);
    }

    [Fact]
    public void Keeps_paragraphs_and_lists_but_drops_markup_images_and_urls()
    {
        const string markdown = """
            # Weather Extended

            Adds **rain** and *snow*. See [the wiki](https://example.com/wiki).

            ![Rain](https://example.com/rain.png)
            <img src="x.png" alt="x"><br>
            - Fog
            * Storms &amp; wind
            """;

        Assert.Equal("Weather Extended\n\nAdds rain and snow. See the wiki.\n\n• Fog\n• Storms & wind",
            ModDescription.Readable(markdown));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Empty_descriptions_are_empty(string? markdown) => Assert.Equal("", ModDescription.Readable(markdown));

    [Fact]
    public void Gallery_takes_https_images_in_order_without_repeats()
    {
        const string markdown = """
            ![one](https://example.com/1.png "title")
            <img alt="two" src="https://example.com/2.jpg">
            ![insecure](http://example.com/3.png)
            ![again](https://example.com/1.png)
            [not an image](https://example.com/page)
            """;

        Assert.Equal(["https://example.com/1.png", "https://example.com/2.jpg"], ModDescription.ImageUrls(markdown));
    }
}
