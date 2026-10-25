using System.Net;
using System.Security.Cryptography;
using Overlord.Core.Catalog;
using Overlord.Infrastructure.Downloads;
using Overlord.Tests.Fixtures;

namespace Overlord.Tests.Downloads;

public sealed class ArchiveDownloaderTests : IDisposable
{
    private static readonly byte[] Good = "PK this stands in for a real .smod archive"u8.ToArray();
    private static readonly byte[] Corrupt = "PK this stands in for a real .smod archivX"u8.ToArray(); // same size, one byte differs
    private static readonly byte[] Short = Good[..10];

    private static readonly VersionTarget Target = new(
        TargetPlatform.Windows, "/v1/version/abc/Windows/download", Good.Length, Convert.ToHexStringLower(SHA256.HashData(Good)));

    private readonly TempDir _dir = new();
    private readonly FakeHttpHandler _handler = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly ArchiveDownloader _downloader;

    public ArchiveDownloaderTests() =>
        _downloader = new ArchiveDownloader(new HttpClient(_handler), _dir.Combine("Archives"),
            delay: (d, _) => { _delays.Add(d); return Task.CompletedTask; });

    public void Dispose() => _dir.Dispose();

    private Task<string> Get(int retries = 5) => _downloader.GetAsync("WeatherExtended", "1.1.0", Target, retries);

    private string[] FilesOnDisk() =>
        Directory.EnumerateFiles(_dir.Combine("Archives"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_dir.Combine("Archives"), f).Replace('\\', '/'))
            .ToArray();

    [Fact]
    public async Task Downloads_and_verifies_into_the_archive_path()
    {
        _handler.RespondBytes(Good);

        var path = await Get();

        Assert.Equal(_dir.Combine("Archives", "WeatherExtended", "1.1.0-Windows.smod"), path);
        Assert.Equal(Good, await File.ReadAllBytesAsync(path));
        Assert.Equal(["WeatherExtended/1.1.0-Windows.smod"], FilesOnDisk());
    }

    [Fact]
    public async Task Hash_mismatch_is_downloaded_again()
    {
        _handler.RespondBytes(Corrupt).RespondBytes(Good);

        var path = await Get();

        Assert.Equal(Good, await File.ReadAllBytesAsync(path));
        Assert.Single(_delays);
    }

    [Fact]
    public async Task Size_mismatch_is_downloaded_again()
    {
        _handler.RespondBytes(Short).RespondBytes(Good.Concat(Good).ToArray()).RespondBytes(Good);

        var path = await Get();

        Assert.Equal(Good, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, _delays.Count);
    }

    [Fact]
    public async Task Network_errors_timeouts_and_server_errors_are_retried_with_backoff()
    {
        _handler.Throw(new HttpRequestException("connection reset"))
            .Throw(new TaskCanceledException("timeout"))
            .Fail(HttpStatusCode.BadGateway)
            .RespondBytes(Good);

        await Get();

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _delays);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_retries_and_leaves_nothing_behind()
    {
        for (var i = 0; i < 3; i++)
            _handler.RespondBytes(Corrupt);

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
    public async Task Attempts_are_one_plus_retries_within_limits(int retries, int expectedAttempts)
    {
        for (var i = 0; i < 10; i++)
            _handler.Throw(new HttpRequestException("offline"));

        var e = await Assert.ThrowsAsync<ArchiveDownloadException>(() => Get(retries));

        Assert.Equal(expectedAttempts, e.Attempts.Count);
        Assert.Equal(10 - expectedAttempts, _handler.Remaining);
    }

    [Fact]
    public async Task Not_found_is_not_retried()
    {
        _handler.Fail(HttpStatusCode.NotFound);

        var e = await Assert.ThrowsAsync<ArchiveDownloadException>(() => Get());

        Assert.Single(e.Attempts);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task A_kept_archive_that_still_matches_is_reused_without_downloading()
    {
        _handler.RespondBytes(Good);
        await Get();

        var path = await Get();

        Assert.Equal(Good, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, _handler.Remaining);
        Assert.Single(_handler.RequestBodies); // only the first call reached the network
    }

    [Fact]
    public async Task A_kept_archive_that_no_longer_matches_is_downloaded_again()
    {
        var path = _downloader.PathFor("WeatherExtended", "1.1.0", TargetPlatform.Windows);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Corrupt);
        _handler.RespondBytes(Good);

        await Get();

        Assert.Equal(Good, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("../Escape", "1.0.0")]
    [InlineData("Mod", "../../1.0.0")]
    [InlineData("Mod", "1.0/0")]
    public async Task Rejects_names_that_could_escape_the_archive_folder(string reference, string version)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _downloader.GetAsync(reference, version, Target, 0));
    }
}
