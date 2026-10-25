using Overlord.Core.Settings;
using Overlord.Infrastructure.Settings;
using Overlord.Tests.Fixtures;

namespace Overlord.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults_with_archives_kept()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.Combine("Config", "settings.json")).Load();

        Assert.Equal(AppSettings.Default, settings);
        Assert.True(settings.KeepDownloadedArchives);
    }

    [Fact]
    public void Saves_and_loads()
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
    public void Damaged_file_gives_defaults(string content)
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("Config/settings.json", content);

        Assert.Equal(AppSettings.Default, new SettingsStore(path).Load());
    }

    [Fact]
    public void Unknown_and_missing_fields_are_tolerated()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("Config/settings.json", """{ "someFutureSetting": 3 }""");

        Assert.True(new SettingsStore(path).Load().KeepDownloadedArchives);
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(3, 3)]
    [InlineData(99, 8)]
    public void Download_retries_are_kept_within_0_to_8(int saved, int expected)
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("Config/settings.json", $$"""{ "downloadRetries": {{saved}} }""");
        var store = new SettingsStore(path);

        Assert.Equal(expected, store.Load().DownloadRetries);

        store.Save(new AppSettings(DownloadRetries: saved));
        Assert.Contains($"\"downloadRetries\": {expected}", File.ReadAllText(path));
    }

    [Fact]
    public void Download_retries_default_to_5()
    {
        using var dir = new TempDir();
        Assert.Equal(5, new SettingsStore(dir.Combine("settings.json")).Load().DownloadRetries);
    }

    [Fact]
    public void Archive_cache_measures_and_deletes_only_its_own_folder()
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
