using System.Collections.Concurrent;
using System.Net;
using Avalonia.Media.Imaging;
using Markdown.Avalonia.Utils;

namespace ADA.Imaging;

/// <summary>Supplies description images through the same bounded HTTPS loader used by the gallery.</summary>
internal sealed class ModDescriptionImageResolver(ModLogos? images) : IPathResolver
{
    private const int IMAGE_WIDTH = 960;

    // Encoded images at display width are a few hundred KB each; a few dozen covers the
    // descriptions a user moves between without growing for a whole session.
    private const int MAX_CACHED = 48;

    // PNG bytes per URL. The renderer needs a fresh stream each time, but re-encoding the cached
    // bitmap on every render is what this avoids.
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _encoded = new(StringComparer.Ordinal);

    // Author images must never resolve relative to an assembly, local folder or document path.
    string? IPathResolver.AssetPathRoot { set { } }

    IEnumerable<string>? IPathResolver.CallerAssemblyNames { set { } }

    /// <summary>
    /// Returns a renderer-owned, read-only stream for a cached image, or null on failure or without a
    /// loader. Local and non-HTTPS paths are rejected by the loader. Encoding runs once per URL off the
    /// UI thread and leaves the shared bitmap owned by the image cache.
    /// </summary>
    public async Task<Stream?> ResolveImageResource(string relativeOrAbsolutePath)
    {
        if (images is null)
        {
            return null;
        }
        var url = WebUtility.HtmlDecode(relativeOrAbsolutePath);
        if (_encoded.Count >= MAX_CACHED)
        {
            _encoded.Clear();
        }
        return await _encoded.GetOrAdd(url, EncodeAsync) is { } png ? new MemoryStream(png, writable: false) : null;
    }

    private async Task<byte[]?> EncodeAsync(string url)
    {
        if (await images!.GetAsync(url, IMAGE_WIDTH) is not { } image)
        {
            return null;
        }
        return await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream();
                image.Save(stream, new PngBitmapEncoderOptions());
                return stream.ToArray();
            }
            catch (Exception)
            {
                // A bitmap that can't be encoded is shown as a missing image, like a failed download.
                return null;
            }
        });
    }
}
