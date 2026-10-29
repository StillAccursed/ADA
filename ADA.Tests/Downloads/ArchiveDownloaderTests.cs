using System.Net;
using System.Security.Cryptography;
using ADA.Core.Catalog;
using ADA.Infrastructure.Downloads;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Downloads;

public sealed class ArchiveDownloaderTests : IDisposable
{
    private static readonly byte[] GOOD = "PK this stands in for a real .smod archive"u8.ToArray();
    private static readonly byte[] CORRUPT = "PK this stands in for a real .smod archivX"u8.ToArray(); // same size, one byte differs
    private static readonly byte[] SHORT = GOOD[..10];

    private static readonly VersionTarget TARGET = new(
        TargetPlatform.Windows, "/v1/version/abc/Windows/download", GOOD.Length, Convert.ToHexStringLower(SHA256.HashData(GOOD)));

    private readonly TempDir _dir = new();
    private readonly FakeHttpHandler _handler = new();
    private readonly InstantTimeProvider _time = new();
    private readonly ArchiveDownloader _downloader;

    public ArchiveDownloaderTests() =>
        _downloader = new ArchiveDownloader(new HttpClient(_handler), _dir.Combine("Archives"), time: _time);

    public void Dispose() => _dir.Dispose();

    private Task<string> Get(int retries = 5) => _downloader.GetAsync("WeatherExtended", "1.1.0", TARGET, retries);

    private string[] FilesOnDisk() =>
        Directory.EnumerateFiles(_dir.Combine("Archives"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_dir.Combine("Archives"), f).Replace('\\', '/'))
            .ToArray();

    [Fact]
    public async Task DownloadsAndVerifiesIntoTheArchivePath()
    {
        _handler.RespondBytes(GOOD);

        var path = await Get();

        Assert.Equal(_dir.Combine("Archives", "WeatherExtended", "1.1.0-Windows.smod"), path);
        Assert.Equal(GOOD, await File.ReadAllBytesAsync(path));
        Assert.Equal(["WeatherExtended/1.1.0-Windows.smod"], FilesOnDisk());
    }

    [Fact]
    public async Task HashMismatchIsDownloadedAgain()
    {
        _handler.RespondBytes(CORRUPT).RespondBytes(GOOD);

        var path = await Get();

        Assert.Equal(GOOD, await File.ReadAllBytesAsync(path));
        Assert.Single(_time.Delays);
    }

    [Fact]
    public async Task SizeMismatchIsDownloadedAgain()
    {
        _handler.RespondBytes(SHORT).RespondBytes(GOOD.Concat(GOOD).ToArray()).RespondBytes(GOOD);

        var path = await Get();

        Assert.Equal(GOOD, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, _time.Delays.Count);
    }

    [Fact]
    public async Task NetworkErrorsTimeoutsAndServerErrorsAreRetriedWithBackoff()
    {
        _handler.Throw(new HttpRequestException("connection reset"))
            .Throw(new TaskCanceledException("timeout"))
            .Fail(HttpStatusCode.BadGateway)
            .RespondBytes(GOOD);

        await Get();

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _time.Delays);
    }

    [Fact]
    public async Task GivesUpAfterTheConfiguredRetriesAndLeavesNothingBehind()
    {
        for (var i = 0; i < 3; i++)
        {
            _handler.RespondBytes(CORRUPT);
        }

        var e = await Assert.ThrowsAsync<ArchiveDownloadException>(() => Get(retries: 2));

        Assert.Equal(3, e.Attempts.Count);
        Assert.All(e.Attempts, a => Assert.Contains("hash mismatch", a));
        Assert.Empty(FilesOnDisk());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5, 6)]
    [InlineData(8, 9)]
    [InlineData(50, 9)] // capped at the maximum of 8 retries
    [InlineData(-3, 1)]
    public async Task AttemptsAreOnePlusRetriesWithinLimits(int retries, int expectedAttempts)
    {
        for (var i = 0; i < 10; i++)
        {
            _handler.Throw(new HttpRequestException("offline"));
        }

        var e = await Assert.ThrowsAsync<ArchiveDownloadException>(() => Get(retries));

        Assert.Equal(expectedAttempts, e.Attempts.Count);
        Assert.Equal(10 - expectedAttempts, _handler.Remaining);
    }

    [Fact]
    public async Task NotFoundIsNotRetried()
    {
        _handler.Fail(HttpStatusCode.NotFound);

        var e = await Assert.ThrowsAsync<ArchiveDownloadException>(() => Get());

        Assert.Single(e.Attempts);
        Assert.Empty(_time.Delays);
    }

    [Fact]
    public async Task AKeptArchiveThatStillMatchesIsReusedWithoutDownloading()
    {
        _handler.RespondBytes(GOOD);
        await Get();

        var path = await Get();

        Assert.Equal(GOOD, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, _handler.Remaining);
        Assert.Single(_handler.RequestBodies); // only the first call reached the network
    }

    [Fact]
    public async Task AKeptArchiveThatNoLongerMatchesIsDownloadedAgain()
    {
        var path = _downloader.PathFor("WeatherExtended", "1.1.0", TargetPlatform.Windows);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, CORRUPT);
        _handler.RespondBytes(GOOD);

        await Get();

        Assert.Equal(GOOD, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("../Escape", "1.0.0")]
    [InlineData("Mod", "../../1.0.0")]
    [InlineData("Mod", "1.0/0")]
    public async Task RejectsNamesThatCouldEscapeTheArchiveFolder(string reference, string version)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _downloader.GetAsync(reference, version, TARGET, 0));
    }

    [Fact]
    public async Task ProgressIsThrottledButAlwaysEndsWithTheFullSize()
    {
        var big = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(big);
        var target = new VersionTarget(TargetPlatform.Windows, "/big", big.Length, Convert.ToHexStringLower(SHA256.HashData(big)));
        _handler.RespondBytes(big);
        var reports = new List<DownloadProgress>();

        await _downloader.GetAsync("Big", "1.0.0", target, 0, new SyncProgress<DownloadProgress>(reports.Add));

        // 4 MB arrives in about 50 buffers; reporting each one would flood the UI thread.
        Assert.InRange(reports.Count, 1, 10);
        Assert.Equal(big.Length, reports[^1].BytesReceived);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task ADownloadThatStopsSendingDataIsRetried()
    {
        var handler = new StallingThenGoodHandler(GOOD);
        var downloader = new ArchiveDownloader(new HttpClient(handler), _dir.Combine("Archives"), time: _time)
        {
            StallTimeout = TimeSpan.FromMilliseconds(200),
        };

        var path = await downloader.GetAsync("WeatherExtended", "1.1.0", TARGET, 1);

        Assert.Equal(GOOD, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, handler.Requests);
    }

    /// <summary>First response sends headers and then nothing, like a dead connection; the second is fine.</summary>
    private sealed class StallingThenGoodHandler(byte[] good) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            HttpContent content = Requests == 1 ? new StreamContent(new NeverEndingStream()) : new ByteArrayContent(good);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class NeverEndingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
