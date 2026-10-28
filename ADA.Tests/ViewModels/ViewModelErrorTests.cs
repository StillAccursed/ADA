using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Settings;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.Ficsit;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Platform.Detection;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;
using ADA.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

/// <summary>Failures behind a button or a background check end up on screen, never as a crash.</summary>
public sealed class ViewModelErrorTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Repair_while_offline_shows_a_notice_instead_of_throwing()
    {
        var game = new GameFolder(_dir.Combine("Satisfactory"));
        var archive = new SmodBuilder("SML", "3.12.0").Typical().WriteTo(_dir.Combine("Archives"));
        new ModInstaller(game.Root, () => false).Apply([new ModChange.Install(archive, TargetPlatform.Windows)]);
        Directory.Delete(game.ModFolder("Mods/SML"), recursive: true);

        var service = new ModInstallService(
            new SettingsStore(_dir.Combine("settings.json")),
            new ArchiveDownloader(new HttpClient(new FakeHttpHandler()), _dir.Combine("Staging")),
            new FakeVersionSource((_, _) => throw new FicsitApiException("Could not reach ficsit.app.")),
            _ => Task.FromResult(false));
        var install = new GameInstall(game.Root, GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "test");
        var vm = new GameInstallViewModel(install, service, new ModResolutionService(new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), service), new GameProfiles(new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json")), service), NullLogger<GameInstallViewModel>.Instance);

        await vm.CheckAsync();
        Assert.True(vm.CanRepair);

        await vm.RepairCommand.ExecuteAsync(null);

        Assert.Contains("Could not reach ficsit.app", vm.Notice);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.ProgressText);
    }

    [Fact]
    public async Task A_damaged_catalogue_database_shows_an_error_instead_of_throwing()
    {
        var path = _dir.Combine("catalog.db");
        var sync = new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), new ModCatalogStore(path));
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(_dir.Path, "catalog.db*"))
        {
            File.Delete(file);
        }

        await File.WriteAllTextAsync(path, "this is not a SQLite database, just text long enough to have a header");

        var vm = new CatalogStatusViewModel(sync, NullLogger<CatalogStatusViewModel>.Instance);
        await vm.LoadAsync();

        Assert.Contains("Couldn't read the saved mod list", vm.ErrorText);
    }

    [Fact]
    public async Task The_game_watcher_keeps_going_after_a_failed_check()
    {
        var calls = 0;
        var dashboard = DashboardFactory.Create(_dir,
            new NoGames(),
            _ => throw new InvalidOperationException("not used"),
            findRunning: _ => ++calls == 1
                ? throw new InvalidOperationException("ps failed")
                : Task.FromResult<IReadOnlyList<GameProcess>>([new GameProcess(42, "FactoryGameSteam")]),
            watchInterval: TimeSpan.FromMilliseconds(20));

        using var stop = new CancellationTokenSource();
        var watching = dashboard.WatchGameAsync(stop.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!dashboard.IsGameRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watching);

        Assert.True(dashboard.IsGameRunning);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task Settings_save_once_after_a_burst_of_changes()
    {
        var store = new SettingsStore(_dir.Combine("Config", "settings.json"));
        var vm = new SettingsViewModel(store, new ArchiveCache(_dir.Combine("Archives")), NullLogger<SettingsViewModel>.Instance);

        vm.DownloadRetries = 1;
        vm.DownloadRetries = 2;
        vm.DownloadRetries = 3;
        Assert.False(File.Exists(store.SettingsPath)); // nothing written while the user is still clicking

        await vm.PendingSave;

        Assert.Equal(3, store.Load().DownloadRetries);
        Assert.Equal(3, store.Current.DownloadRetries);
        Assert.Null(vm.ErrorText);
    }

    private sealed class NoGames : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => [];
    }
}
