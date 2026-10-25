using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Overlord.Core.Catalog;
using Overlord.Core.Settings;
using Overlord.Infrastructure.Ficsit;
using Overlord.Infrastructure.Net;
using Polly;
using Polly.Retry;

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
    private const int BufferSize = 81920;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    // No total time limit (a multi-GB mod on a slow line takes as long as it takes), but a
    // connection that sends nothing for this long is dead and the attempt is retried.
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);

    // Often enough for a smooth progress bar, rarely enough not to flood the UI thread.
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public string ArchivesFolder { get; }

    /// <summary>How long a download may go without receiving any data before the attempt fails.</summary>
    public TimeSpan StallTimeout { get; init; } = DefaultStallTimeout;

    /// <param name="time">Drives retry waits; tests pass one that doesn't sleep.</param>
    public ArchiveDownloader(HttpClient http, string archivesFolder, Uri? baseAddress = null, TimeProvider? time = null,
        ILogger<ArchiveDownloader>? log = null)
    {
        _http = http;
        ArchivesFolder = archivesFolder;
        _baseAddress = baseAddress ?? FicsitApi.BaseAddress;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger<ArchiveDownloader>.Instance;
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

        retries = Math.Clamp(retries, AppSettings.MinDownloadRetries, AppSettings.MaxDownloadRetries);
        var maxAttempts = 1 + retries;
        var failures = new List<string>();
        try
        {
            await BuildPipeline(retries).ExecuteAsync(async token =>
            {
                var attempt = failures.Count + 1;
                try
                {
                    await DownloadOnceAsync(path, target, attempt, maxAttempts, progress, token);
                }
                catch (Exception e) when (!token.IsCancellationRequested)
                {
                    failures.Add($"Attempt {attempt}: {Describe(e)}");
                    _log.LogWarning("Download of {Mod} {Version} failed, attempt {Attempt} of {Max}: {Reason}",
                        modReference, version, attempt, maxAttempts, Describe(e));
                    throw;
                }
            }, ct);
            return path;
        }
        catch (Exception e) when (IsAttemptFailure(e, ct))
        {
            throw new ArchiveDownloadException(
                $"Could not download {modReference} {version} ({target.Platform}) after {failures.Count} attempt(s).",
                failures);
        }
    }

    private ResiliencePipeline BuildPipeline(int retries)
    {
        var builder = new ResiliencePipelineBuilder { TimeProvider = _time };
        if (retries > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = retries,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is AttemptFailedException { Permanent: false }
                    || Retry.IsTransientException(args.Outcome.Exception, args.Context)
                    || args.Outcome.Exception is IOException),
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(Retry.Backoff(args.AttemptNumber,
                    (args.Outcome.Exception as AttemptFailedException)?.RetryAfter, MaxBackoff)),
            });
        }
        return builder.Build();
    }

    private async Task DownloadOnceAsync(string path, VersionTarget target, int attempt, int maxAttempts,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var part = path + ".part";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            stall.CancelAfter(StallTimeout);
            using var response = await _http.GetAsync(new Uri(_baseAddress, target.DownloadPath),
                HttpCompletionOption.ResponseHeadersRead, stall.Token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new AttemptFailedException("server busy.", retryAfter: response.Headers.RetryAfter?.Delta ?? TimeSpan.Zero);

            if (!response.IsSuccessStatusCode)
            {
                // Server errors and timeouts can pass; "not found" or "forbidden" will not.
                var status = (int)response.StatusCode;
                var permanent = status is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout;
                throw new AttemptFailedException($"HTTP {status}.", permanent);
            }

            long received = 0;
            var lastReport = _time.GetTimestamp();
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout);
                        if ((read = await source.ReadAsync(buffer, stall.Token)) == 0)
                            break;

                        received += read;
                        if (target.Size is { } expected && received > expected)
                            throw new AttemptFailedException($"size mismatch (more than the expected {expected:N0} bytes).");

                        hash.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        if (progress is not null && _time.GetElapsedTime(lastReport) >= ProgressInterval)
                        {
                            progress.Report(new DownloadProgress(attempt, maxAttempts, received, target.Size));
                            lastReport = _time.GetTimestamp();
                        }
                    }
                    await file.FlushAsync(ct);
                    file.Flush(flushToDisk: true);
                }
                progress?.Report(new DownloadProgress(attempt, maxAttempts, received, target.Size));

                if (target.Size is { } size && received != size)
                    throw new AttemptFailedException($"size mismatch ({received:N0} of {size:N0} bytes).");

                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (target.Hash is { } expectedHash && !string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new AttemptFailedException("hash mismatch.");
            }

            File.Move(part, path, overwrite: true);
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new AttemptFailedException($"no data for {StallTimeout.TotalSeconds:N0} seconds.");
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

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return string.Equals(actual, target.Hash, StringComparison.OrdinalIgnoreCase) ? null : "hash mismatch";
    }

    private static bool IsAttemptFailure(Exception e, CancellationToken ct) =>
        e is AttemptFailedException or HttpRequestException or IOException
        || (e is TaskCanceledException && !ct.IsCancellationRequested);

    private static string Describe(Exception e) => e switch
    {
        AttemptFailedException => e.Message,
        TaskCanceledException => "timed out.",
        _ => e.Message.EndsWith('.') ? e.Message : e.Message + ".",
    };

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

    private sealed class AttemptFailedException(string message, bool permanent = false, TimeSpan? retryAfter = null)
        : Exception(message)
    {
        public bool Permanent { get; } = permanent;

        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}
