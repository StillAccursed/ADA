using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Imaging;

/// <summary>
/// Downloads and decodes mod logos and gallery images, once per URL and size while the app runs.
/// Images are decoded at the width they're shown at, to keep memory flat.
/// </summary>
public sealed class ModLogos(HttpClient http, ILogger<ModLogos>? log = null)
{

    // Logos are uploaded by mod authors; anything this large is not a logo.
    private const long MaxBytes = 8L * 1024 * 1024;

    private const int MaxCached = 400;

    private readonly ILogger _log = log ?? NullLogger<ModLogos>.Instance;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new(StringComparer.Ordinal);

    // A page of 200 cards shouldn't open 200 connections at once.
    private readonly SemaphoreSlim _downloads = new(6, 6);

    /// <param name="width">Decoded width in pixels: small for logos, larger for the mod page's gallery.</param>
    /// <returns>The image, or null when there is none or it couldn't be loaded. Never throws.</returns>
    public Task<Bitmap?> GetAsync(string? url, int width = 256)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return Task.FromResult<Bitmap?>(null);

        if (_cache.Count >= MaxCached)
            _cache.Clear();
        return _cache.GetOrAdd($"{width}|{uri.AbsoluteUri}", key => LoadAsync(uri, width, key));
    }

    private async Task<Bitmap?> LoadAsync(Uri uri, int width, string key)
    {
        await _downloads.WaitAsync();
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
            return await Task.Run(() => Bitmap.DecodeToWidth(buffer, width));
        }
        catch (Exception e)
        {
            // A missing logo is cosmetic: log it and show the placeholder.
            _log.LogDebug(e, "Couldn't load logo {Url}", uri);
            _cache.TryRemove(key, out _);
            return null;
        }
        finally
        {
            _downloads.Release();
        }
    }
}
