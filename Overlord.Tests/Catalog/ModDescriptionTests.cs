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
}
