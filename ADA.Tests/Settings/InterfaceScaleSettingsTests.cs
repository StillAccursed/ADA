using ADA.Core.Settings;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Settings;

public sealed class InterfaceScaleSettingsTests : IDisposable
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
    public void Scale_is_normalised_and_round_trips(int scale, int expected)
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        store.Save(new(InterfaceScale: scale));
        Assert.Equal(expected, new SettingsStore(store.SettingsPath).Current.InterfaceScale);
    }

    [Fact]
    public void Older_settings_use_full_size()
    {
        var path = _dir.WriteFile("settings.json", "{\"keepDownloadedArchives\":false}");
        Assert.Equal(AppSettings.DEFAULT_INTERFACE_SCALE, new SettingsStore(path).Current.InterfaceScale);
    }

    [Fact]
    public async Task Debounced_scale_save_preserves_another_pages_latest_preferences()
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        var vm = new SettingsViewModel(store, new(_dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance);
        vm.InterfaceScale = 125;
        store.Update(s => s with { ModsView = "Grid" });
        await vm.PendingSave;
        Assert.Equal(125, store.Current.InterfaceScale);
        Assert.Equal("Grid", store.Current.ModsView);
    }

    [Fact]
    public async Task Unsupported_scale_from_the_page_is_not_saved()
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        var vm = new SettingsViewModel(store, new(_dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance);
        vm.InterfaceScale = 133;
        await vm.PendingSave;
        Assert.Equal(AppSettings.DEFAULT_INTERFACE_SCALE, store.Current.InterfaceScale);
    }
}
