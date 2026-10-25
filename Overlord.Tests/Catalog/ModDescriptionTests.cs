using Overlord.Infrastructure.Catalog;

namespace Overlord.Tests.Catalog;

public class ModDescriptionTests
{
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
