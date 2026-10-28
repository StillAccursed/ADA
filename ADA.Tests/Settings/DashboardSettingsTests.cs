using ADA.Core.Settings;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Settings;

public sealed class DashboardSettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    [InlineData(999, 20)]
    public void Count_is_normalised_and_round_trips(int count, int expected)
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        store.Save(new(DashboardLatestModsCount: count));
        Assert.Equal(expected, new SettingsStore(store.SettingsPath).Current.DashboardLatestModsCount);
    }

    [Fact]
    public async Task Debounced_dashboard_save_preserves_another_pages_latest_preferences()
    {
        var store = new SettingsStore(_dir.Combine("settings.json"));
        var vm = new SettingsViewModel(store, new(_dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance);
        vm.DashboardLatestModsCount = 12;
        store.Update(s => s with { ModsView = "Grid", ModsSort = "Updated" });
        await vm.PendingSave;
        Assert.Equal(12, store.Current.DashboardLatestModsCount);
        Assert.Equal("Grid", store.Current.ModsView);
        Assert.Equal("Updated", store.Current.ModsSort);
        vm.DashboardLatestModsCount = null;
        await vm.PendingSave;
        Assert.Equal(12, store.Current.DashboardLatestModsCount);
    }

    [Fact]
    public void Older_settings_use_five_releases()
    {
        var path = _dir.WriteFile("settings.json", "{\"keepDownloadedArchives\":false}");
        var settings = new SettingsStore(path).Current;
        Assert.Equal(AppSettings.DEFAULT_DASHBOARD_LATEST_MODS_COUNT, settings.DashboardLatestModsCount);
        Assert.False(settings.KeepDownloadedArchives);
    }
}
