using ADA.Core.Settings;
using ADA.Infrastructure.Logging;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Settings;

public sealed class LogLimitSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(500, 500)]
    [InlineData(5120, 5120)]
    [InlineData(3000, AppSettings.DEFAULT_MAX_LOG_FOLDER_MEGABYTES)]
    [InlineData(0, AppSettings.DEFAULT_MAX_LOG_FOLDER_MEGABYTES)]
    public void UnofferedLimitsFallBackToTheDefault(int saved, int expected)
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        store.Save(new(MaxLogFolderMegabytes: saved));

        Assert.Equal(expected, new SettingsStore(store.SettingsPath).Current.MaxLogFolderMegabytes);
    }

    [Fact]
    public async Task LoweringTheLimitSavesItAndDeletesTheOldestLogsOverIt()
    {
        var logs = _dir.Combine("Logs");
        var oneMegabyte = new string('x', 1024 * 1024);
        for (var day = 25; day <= 31; day++)
        {
            var path = _dir.WriteFile($"Logs/ada-202610{day}.log", oneMegabyte + new string('x', 20 * 1024 * 1024));
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc));
        }
        var store = new SettingsStore(_dir.Combine("settings.json"));
        var page = new SettingsViewModel(store, new ArchiveCache(_dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance,
            new LogFiles(logs));
        await page.RefreshAsync();
        Assert.StartsWith("Logs use 147 MB (7 files)", page.LogsSummary);

        page.LogFolderLimit = page.LogFolderLimits.Single(o => o.Megabytes == 100);
        await page.PendingSave;

        Assert.Equal(100, store.Current.MaxLogFolderMegabytes);
        Assert.Equal(["ada-20261028.log", "ada-20261029.log", "ada-20261030.log", "ada-20261031.log"],
            Directory.GetFiles(logs).Select(Path.GetFileName).Order());
        Assert.StartsWith("Logs use 84 MB (4 files)", page.LogsSummary);
    }

    [Fact]
    public void OptionsReadAsSizes()
    {
        Assert.Equal(["100 MB", "250 MB", "500 MB", "1 GB", "2 GB", "5 GB"],
            AppSettings.LOG_FOLDER_LIMITS.Select(mb => new LogLimitOption(mb).ToString()));
    }
}
