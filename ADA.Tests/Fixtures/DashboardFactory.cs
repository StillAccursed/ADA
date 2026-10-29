using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Ficsit;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Servers;
using ADA.Infrastructure.Settings;
using ADA.Platform.Detection;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Fixtures;

/// <summary>
/// Builds a <see cref="DashboardViewModel"/> for tests. Services a test doesn't pass are created in
/// <paramref name="dir"/> with offline fakes, so each test supplies only what it observes.
/// </summary>
internal static class DashboardFactory
{
    public static DashboardViewModel Create(TempDir dir, IGameDetector detector,
        Func<GameInstall, GameInstallViewModel> createInstall,
        CatalogStatusViewModel? catalog = null, ModCatalogStore? store = null, SettingsStore? settings = null,
        ModInstallService? installs = null, GameProfiles? profiles = null, ModResolutionService? resolution = null,
        Func<CancellationToken, Task<IReadOnlyList<GameProcess>>>? findRunning = null, TimeSpan? watchInterval = null,
        ModChangelogs? changelogs = null, ServerConnections? servers = null, ProfileSync? sync = null, ConfigPush? configPush = null)
    {
        store ??= new ModCatalogStore(dir.Combine("dashboard-catalog.db"));
        settings ??= new SettingsStore(dir.Combine("dashboard-settings.json"));
        catalog ??= new CatalogStatusViewModel(new CatalogSync(new FicsitApiClient(new HttpClient(new FakeHttpHandler())), store),
            NullLogger<CatalogStatusViewModel>.Instance);
        installs ??= new ModInstallService(settings, new(new HttpClient(new FakeHttpHandler()), dir.Combine("Archives")),
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), (_, _) => Task.FromResult(false));
        profiles ??= new GameProfiles(new ProfileStore(dir.Combine("Profiles"), dir.Combine("profiles.json")), installs);
        resolution ??= new ModResolutionService(new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), installs);
        return new DashboardViewModel(detector, catalog, createInstall, NullLogger<DashboardViewModel>.Instance,
            store, settings, profiles, resolution, new DashboardStorageReader(dir.Combine("Archives")), installs,
            new ArchiveCache(dir.Combine("Archives")),
            changelogs ?? new ModChangelogs(new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([]))),
            findRunning ?? (_ => Task.FromResult<IReadOnlyList<GameProcess>>([])),
            watchInterval, servers, sync, configPush);
    }
}
