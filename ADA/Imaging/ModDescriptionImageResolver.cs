using System.Net;
using Avalonia.Media.Imaging;
using Markdown.Avalonia.Utils;

namespace ADA.Imaging;

/// <summary>Supplies description images through the same bounded HTTPS loader used by the gallery.</summary>
internal sealed class ModDescriptionImageResolver(ModLogos? images) : IPathResolver
{
    private const int IMAGE_WIDTH = 960;

    // Author images must never resolve relative to an assembly, local folder or document path.
    string? IPathResolver.AssetPathRoot { set { } }

    IEnumerable<string>? IPathResolver.CallerAssemblyNames { set { } }

    /// <summary>
    /// Returns a renderer-owned stream for a cached image, or null on failure or without a loader.
    /// The renderer accepts streams rather than bitmaps; encoding runs off the UI thread and leaves
    /// the shared bitmap owned by the image cache. Local and non-HTTPS paths are rejected by the loader.
    /// </summary>
    public async Task<Stream?> ResolveImageResource(string relativeOrAbsolutePath)
    {
        if (images is null || await images.GetAsync(WebUtility.HtmlDecode(relativeOrAbsolutePath), IMAGE_WIDTH) is not { } image)
        {
            return null;
        }

        return await Task.Run<Stream?>(() =>
        {
            var stream = new MemoryStream();
            try
            {
                image.Save(stream, new PngBitmapEncoderOptions());
                stream.Position = 0;
                return stream;
            }
            catch
            {
                stream.Dispose();
                return null;
            }
        });
    }
}
