using System.Net;
using System.Security.Cryptography;
using Overlord.Core.Catalog;
using Overlord.Core.Settings;

namespace Overlord.Infrastructure.Downloads;

public sealed record DownloadProgress(int Attempt, int MaxAttempts, long BytesReceived, long? TotalBytes);

/// <summary>Every attempt failed; <see cref="Attempts"/> says why each one did.</summary>
public sealed class ArchiveDownloadException(string message, IReadOnlyList<string> attempts)
    : Exception(message + " " + string.Join(" ", attempts))
{
    public IReadOnlyList<string> Attempts { get; } = attempts;
}

/// <summary>
/// Downloads <c>.smod</c> archives into <c>Staging/Archives/&lt;Reference&gt;/&lt;Version&gt;-&lt;Target&gt;.smod</c>
/// and only ever hands back a file whose size and SHA-256 match the ficsit.app API.
/// </summary>
/// <remarks>
/// A failed attempt (network error, timeout, server error, wrong size, wrong hash) is retried up to
/// the user's configured count. Wrong size and wrong hash always mean "download again", including
/// for an archive already on disk from an earlier session.
/// </remarks>
public sealed class ArchiveDownloader
{
    private static readonly Uri DefaultBaseAddress = new("https://api.ficsit.app/");
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public string ArchivesFolder { get; }

    /// <param name="delay">Backoff wait; replaced in tests so retries don't sleep.</param>
    public ArchiveDownloader(HttpClient http, string archivesFolder, Uri? baseAddress = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        ArchivesFolder = archivesFolder;
        _baseAddress = baseAddress ?? DefaultBaseAddress;
        _delay = delay ?? Task.Delay;
    }

    public string PathFor(string modReference, string version, TargetPlatform platform) =>
        Path.Combine(ArchivesFolder, SafeName(modReference), $"{SafeName(version)}-{platform}.smod");

    /// <param name="retries">Retries after the first attempt; clamped to 0–8.</param>
    /// <returns>Path to a verified archive.</returns>
    public async Task<string> GetAsync(string modReference, string version, VersionTarget target, int retries,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var path = PathFor(modReference, version, target.Platform);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // A kept archive is reused only if it still matches; otherwise it is replaced.
        if (File.Exists(path))
        {
            if (await VerifyAsync(path, target, ct) is null)
                return path;
            File.Delete(path);
        }

        var maxAttempts = 1 + Math.Clamp(retries, AppSettings.MinDownloadRetries, AppSettings.MaxDownloadRetries);
        var failures = new List<string>();
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            TimeSpan? retryAfter = null;
            try
            {
                retryAfter = await DownloadOnceAsync(path, target, attempt, maxAttempts, progress, ct);
                if (retryAfter is null)
                    return path;
                failures.Add($"Attempt {attempt}: server busy.");
            }
            catch (AttemptFailedException e)
            {
                failures.Add($"Attempt {attempt}: {e.Message}");
                if (e.Permanent)
                    break;
            }
            catch (Exception e) when (e is HttpRequestException or IOException
                                          || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                failures.Add($"Attempt {attempt}: {(e is TaskCanceledException ? "timed out" : e.Message)}.");
            }

            if (attempt < maxAttempts)
                await _delay(Backoff(attempt, retryAfter), ct);
        }

        throw new ArchiveDownloadException(
            $"Could not download {modReference} {version} ({target.Platform}) after {failures.Count} attempt(s).",
            failures);
    }

    /// <returns>Null on success; a delay to wait when the server asked us to slow down.</returns>
    private async Task<TimeSpan?> DownloadOnceAsync(string path, VersionTarget target, int attempt, int maxAttempts,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var part = path + ".part";
        try
        {
            using var response = await _http.GetAsync(new Uri(_baseAddress, target.DownloadPath),
                HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero;

            if (!response.IsSuccessStatusCode)
            {
                // Server errors and timeouts can pass; "not found" or "forbidden" will not.
                var status = (int)response.StatusCode;
                var permanent = status is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout;
                throw new AttemptFailedException($"HTTP {status}.", permanent);
            }

            long received = 0;
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        received += read;
                        if (target.Size is { } expected && received > expected)
                            throw new AttemptFailedException($"size mismatch (more than the expected {expected:N0} bytes).");

                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        progress?.Report(new DownloadProgress(attempt, maxAttempts, received, target.Size));
                    }
                    await file.FlushAsync(ct);
                    file.Flush(flushToDisk: true);
                }

                if (target.Size is { } size && received != size)
                    throw new AttemptFailedException($"size mismatch ({received:N0} of {size:N0} bytes).");

                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (target.Hash is { } expectedHash && !string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new AttemptFailedException("hash mismatch.");
            }

            File.Move(part, path, overwrite: true);
            return null;
        }
        finally
        {
            // Never leave a partial or rejected download behind.
            if (File.Exists(part))
                File.Delete(part);
        }
    }

    /// <returns>Null if the file matches; otherwise why not.</returns>
    public static async Task<string?> VerifyAsync(string path, VersionTarget target, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (target.Size is { } size && info.Length != size)
            return "size mismatch";
        if (target.Hash is null)
            return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(actual, target.Hash, StringComparison.OrdinalIgnoreCase) ? null : "hash mismatch";
    }

    private static TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var exponential = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        var wait = retryAfter is { } ra && ra > exponential ? ra : exponential;
        return wait < MaxBackoff ? wait : MaxBackoff;
    }

    // Mod references and versions come from the API; refuse anything that could escape the folder.
    private static string SafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("..") || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new ArgumentException($"'{value}' cannot be used in a file name.");
        }
        return value;
    }

    private sealed class AttemptFailedException(string message, bool permanent = false) : Exception(message)
    {
        public bool Permanent { get; } = permanent;
    }
}
