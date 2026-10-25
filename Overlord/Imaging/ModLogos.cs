using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Overlord.Imaging;

/// <summary>
/// Downloads and decodes mod logos for the Mods page, once per URL while the app runs. Logos are
/// shown small, so they are decoded at <see cref="DecodeWidth"/> to keep memory flat.
/// </summary>
public sealed class ModLogos(HttpClient http, ILogger<ModLogos>? log = null)
{
    private const int DecodeWidth = 256;

    // Logos are uploaded by mod authors; anything this large is not a logo.
    private const long MaxBytes = 8L * 1024 * 1024;

    private const int MaxCached = 400;

    private readonly ILogger _log = log ?? NullLogger<ModLogos>.Instance;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new(StringComparer.Ordinal);

    /// <returns>The logo, or null when there is none or it couldn't be loaded. Never throws.</returns>
    public Task<Bitmap?> GetAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult<Bitmap?>(null);

        if (_cache.Count >= MaxCached)
            _cache.Clear();
        return _cache.GetOrAdd(uri.AbsoluteUri, _ => LoadAsync(uri));
    }

    private async Task<Bitmap?> LoadAsync(Uri uri)
    {
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            return await Task.Run(() => Bitmap.DecodeToWidth(buffer, DecodeWidth));
        }
        catch (Exception e)
        {
            // A missing logo is cosmetic: log it and show the placeholder.
            _log.LogDebug(e, "Couldn't load logo {Url}", uri);
            _cache.TryRemove(uri.AbsoluteUri, out _);
            return null;
        }
    }
}
