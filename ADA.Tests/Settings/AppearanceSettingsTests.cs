using ADA.Core.Settings;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Settings;

public sealed class AppearanceSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(75, 75)]
    [InlineData(125, 125)]
    [InlineData(150, 150)]
    [InlineData(133, 100)]
    [InlineData(0, 100)]
    [InlineData(400, 100)]
    public void ScaleIsNormalisedAndRoundTrips(int scale, int expected)
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        store.Save(new(InterfaceScale: scale));
        Assert.Equal(expected, new SettingsStore(store.SettingsPath).Current.InterfaceScale);
    }

    [Theory]
    [InlineData("System", "System")]
    [InlineData("Light", "Light")]
    [InlineData("Dark", "Dark")]
    [InlineData("Purple", "System")]
    [InlineData("", "System")]
    [InlineData("dark", "System")]
    public void ThemeIsNormalisedAndRoundTrips(string theme, string expected)
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        store.Save(new(Theme: theme));
        Assert.Equal(expected, new SettingsStore(store.SettingsPath).Current.Theme);
    }

    [Fact]
    public void UnknownThemeInFileKeepsTheOtherSettings()
    {
        var path = _dir.WriteFile("settings.json", "{\"theme\":\"Purple\",\"interfaceScale\":125,\"keepDownloadedArchives\":false}");
        var settings = new SettingsStore(path).Current;
        Assert.Equal(AppSettings.THEME_SYSTEM, settings.Theme);
        Assert.Equal(125, settings.InterfaceScale);
        Assert.False(settings.KeepDownloadedArchives);
    }

    [Fact]
    public void OlderSettingsUseFullSizeAndTheSystemTheme()
    {
        var path = _dir.WriteFile("settings.json", "{\"keepDownloadedArchives\":false}");
        var settings = new SettingsStore(path).Current;
        Assert.Equal(AppSettings.DEFAULT_INTERFACE_SCALE, settings.InterfaceScale);
        Assert.Equal(AppSettings.THEME_SYSTEM, settings.Theme);
    }

    [Fact]
    public async Task DebouncedAppearanceSavePreservesAnotherPagesLatestPreferences()
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        var vm = new SettingsViewModel(store, new(_dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance);
        vm.InterfaceScale = 125;
        vm.Theme = AppSettings.THEME_DARK;
        store.Update(s => s with { ModsView = "Grid" });
        await vm.PendingSave;
        Assert.Equal(125, store.Current.InterfaceScale);
        Assert.Equal(AppSettings.THEME_DARK, store.Current.Theme);
        Assert.Equal("Grid", store.Current.ModsView);
    }

    [Fact]
    public async Task UnsupportedChoicesFromThePageAreNotSaved()
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        var vm = new SettingsViewModel(store, new(_dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance);
        vm.InterfaceScale = 133;
        vm.Theme = "Purple";
        await vm.PendingSave;
        Assert.Equal(AppSettings.DEFAULT_INTERFACE_SCALE, store.Current.InterfaceScale);
        Assert.Equal(AppSettings.THEME_SYSTEM, store.Current.Theme);
    }
}
