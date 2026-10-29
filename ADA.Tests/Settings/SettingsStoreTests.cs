using ADA.Core.Settings;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void MissingFileGivesDefaultsWithArchivesKept()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.Combine("Config", "settings.json")).Load();

        Assert.Equal(AppSettings.Default, settings);
        Assert.True(settings.KeepDownloadedArchives);
    }

    [Fact]
    public void SavesAndLoads()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Combine("Config", "settings.json"));

        store.Save(new AppSettings(KeepDownloadedArchives: false));

        Assert.False(store.Load().KeepDownloadedArchives);
        Assert.Contains("\"keepDownloadedArchives\": false", File.ReadAllText(store.SettingsPath));
        Assert.False(File.Exists(store.SettingsPath + ".tmp"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    public void DamagedFileGivesDefaults(string content)
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("Config/settings.json", content);

        Assert.Equal(AppSettings.Default, new SettingsStore(path).Load());
    }

    [Fact]
    public void UnknownAndMissingFieldsAreTolerated()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("Config/settings.json", """{ "someFutureSetting": 3 }""");

        Assert.True(new SettingsStore(path).Load().KeepDownloadedArchives);
    }

    [Fact]
    public async Task OlderSettingsCheckServerUploadsAndThePageSavesTheChoice()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("Config/settings.json", """{ "keepDownloadedArchives": false }""");
        var store = new SettingsStore(path);
        Assert.True(store.Current.CheckServerUploads);

        var page = new SettingsViewModel(store, new(dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance);
        Assert.True(page.CheckServerUploads);
        page.CheckServerUploads = false;
        await page.PendingSave;

        Assert.False(store.Current.CheckServerUploads);
        Assert.False(store.Current.KeepDownloadedArchives);
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(3, 3)]
    [InlineData(99, 8)]
    public void DownloadRetriesAreKeptWithin0To8(int saved, int expected)
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("Config/settings.json", $$"""{ "downloadRetries": {{saved}} }""");
        var store = new SettingsStore(path);

        Assert.Equal(expected, store.Load().DownloadRetries);

        store.Save(new AppSettings(DownloadRetries: saved));
        Assert.Contains($"\"downloadRetries\": {expected}", File.ReadAllText(path));
    }

    [Fact]
    public void DownloadRetriesDefaultTo5()
    {
        using var dir = new TempDir();
        Assert.Equal(5, new SettingsStore(dir.Combine("settings.json")).Load().DownloadRetries);
    }

    [Fact]
    public void OlderSettingsFilesEnableEndlessScrollAndDefaultTo50PerPage()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("settings.json", """{ "downloadRetries": 3, "modsView": "Grid" }""");
        var settings = new SettingsStore(path).Load();

        Assert.True(settings.EndlessScroll);
        Assert.Equal(50, settings.ModsPageSize);
        Assert.Equal(3, settings.DownloadRetries);
        Assert.Equal("Grid", settings.ModsView);
    }

    [Theory]
    [InlineData(25, 25)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(200, 200)]
    [InlineData(500, 500)]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(75, 50)]
    [InlineData(501, 50)]
    public void BrowsingPreferencesRoundTripAndUnsupportedPageSizesUse50(int size, int expected)
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("settings.json", $$"""{ "endlessScroll": false, "modsPageSize": {{size}} }""");
        var store = new SettingsStore(path);

        Assert.False(store.Load().EndlessScroll);
        Assert.Equal(expected, store.Load().ModsPageSize);
        store.Save(new AppSettings(EndlessScroll: false, ModsPageSize: size));
        Assert.False(store.Load().EndlessScroll);
        Assert.Equal(expected, store.Load().ModsPageSize);
        Assert.Contains($"\"modsPageSize\": {expected}", File.ReadAllText(path));
    }

    [Fact]
    public void ChangeNotificationsSeeSuccessfullyWrittenNormalisedSettings()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Combine("settings.json"));
        var notifications = new List<AppSettings>();
        store.Changed += (_, _) =>
        {
            Assert.Equal(store.Load(), store.Current);
            notifications.Add(store.Current);
        };

        store.Save(new AppSettings(EndlessScroll: false, ModsPageSize: 75));
        store.Update(current => current with { ModsView = "Card" });

        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, settings => Assert.False(settings.EndlessScroll));
        Assert.All(notifications, settings => Assert.Equal(50, settings.ModsPageSize));
        Assert.Equal("Card", notifications[1].ModsView);
    }

    [Fact]
    public void AFailedWriteKeepsCurrentSettingsAndDoesNotNotify()
    {
        using var dir = new TempDir();
        var blocked = dir.WriteFile("blocked", "file instead of a folder");
        var store = new SettingsStore(Path.Combine(blocked, "settings.json"));
        var before = store.Current;
        var notified = false;
        store.Changed += (_, _) => notified = true;

        Assert.ThrowsAny<IOException>(() => store.Save(new AppSettings(EndlessScroll: false)));

        Assert.Equal(before, store.Current);
        Assert.False(notified);
    }

    [Fact]
    public void ArchiveCacheMeasuresAndDeletesOnlyItsOwnFolder()
    {
        using var dir = new TempDir();
        dir.WriteFile("Staging/Archives/WeatherExtended/1.1.0-Windows.smod", new string('x', 1000));
        dir.WriteFile("Staging/Archives/SML/3.12.0-Windows.smod", new string('x', 500));
        dir.WriteFile("Staging/other.txt", "keep me");
        var cache = new ArchiveCache(dir.Combine("Staging", "Archives"));

        Assert.Equal((2, 1500L), cache.Measure());

        cache.DeleteAll();

        Assert.Equal((0, 0L), cache.Measure());
        Assert.True(Directory.Exists(cache.Folder));
        Assert.True(File.Exists(dir.Combine("Staging", "other.txt")));
    }
}
