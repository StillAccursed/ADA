using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ADA.Core.Games;
using ADA.Imaging;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.Ficsit;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Platform.Detection;
using ADA.Platform.Storage;
using ADA.ViewModels;
using Serilog;

namespace ADA;

/// <summary>The composition root: every long-lived service and page, built once at startup.</summary>
public static class Services
{
    private const string ApiClient = "ficsit-api";
    private const string DownloadClient = "ficsit-downloads";

    public static ServiceProvider Build(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(logging => logging.AddSerilog(dispose: true));

        // API queries are small and should fail fast. Downloads can be many GB, so they have no
        // total limit; ArchiveDownloader instead retries a connection that stops sending data.
        services.AddHttpClient(ApiClient, http => Configure(http, TimeSpan.FromSeconds(30)));
        services.AddHttpClient(DownloadClient, http => Configure(http, Timeout.InfiniteTimeSpan));

        services.AddSingleton(sp => new FicsitApiClient(Http(sp, ApiClient)));
        services.AddSingleton(sp => new ModCatalogStore(paths.CatalogDatabase));
        services.AddSingleton(sp => new CatalogSync(sp.GetRequiredService<FicsitApiClient>(), sp.GetRequiredService<ModCatalogStore>(),
            log: sp.GetRequiredService<ILogger<CatalogSync>>()));
        services.AddSingleton(sp => new SettingsStore(paths.SettingsFile));
        services.AddSingleton(_ => new FavouritesStore(paths.FavouritesFile));
        services.AddSingleton(sp => new ArchiveCache(paths.Archives));
        services.AddSingleton(_ => new DashboardStorageReader(paths.Archives));
        services.AddSingleton(sp => new ArchiveDownloader(Http(sp, DownloadClient), paths.Archives,
            log: sp.GetRequiredService<ILogger<ArchiveDownloader>>()));
        services.AddSingleton(sp => new ModInstallService(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ArchiveDownloader>(),
            sp.GetRequiredService<CatalogSync>().GetVersionsAsync,
            GameProcesses.IsAnyRunningAsync,
            sp.GetRequiredService<ILogger<ModInstallService>>()));
        services.AddSingleton(sp => new ModResolutionService(
            sp.GetRequiredService<CatalogSync>().GetVersionsAsync,
            sp.GetRequiredService<ModInstallService>(),
            sp.GetRequiredService<ILogger<ModResolutionService>>()));
        services.AddSingleton(_ => new ProfileStore(paths.Profiles, paths.ProfilesState));
        services.AddSingleton(sp => new GameProfiles(
            sp.GetRequiredService<ProfileStore>(),
            sp.GetRequiredService<ModInstallService>(),
            sp.GetRequiredService<ILogger<GameProfiles>>()));
        services.AddSingleton(_ => GameDetection.ForCurrentOS());
        services.AddSingleton(sp => new ModLogos(Http(sp, ApiClient), sp.GetRequiredService<ILogger<ModLogos>>()));

        services.AddSingleton<CatalogStatusViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<Func<GameInstall, GameInstallViewModel>>(sp => install => new GameInstallViewModel(
            install, sp.GetRequiredService<ModInstallService>(), sp.GetRequiredService<ModResolutionService>(),
            sp.GetRequiredService<GameProfiles>(),
            sp.GetRequiredService<ILogger<GameInstallViewModel>>()));
        services.AddSingleton(sp =>
        {
            var dashboard = new DashboardViewModel(
                sp.GetRequiredService<IGameDetector>(), sp.GetRequiredService<CatalogStatusViewModel>(),
                sp.GetRequiredService<Func<GameInstall, GameInstallViewModel>>(), sp.GetRequiredService<ILogger<DashboardViewModel>>(),
                store: sp.GetRequiredService<ModCatalogStore>(), settings: sp.GetRequiredService<SettingsStore>(),
                profiles: sp.GetRequiredService<GameProfiles>(), resolution: sp.GetRequiredService<ModResolutionService>(),
                storage: sp.GetRequiredService<DashboardStorageReader>(), installs: sp.GetRequiredService<ModInstallService>());
            sp.GetRequiredService<SettingsViewModel>().ArchivesChanged += (_, _) => _ = dashboard.RefreshCardsAsync();
            return dashboard;
        });
        services.AddSingleton(sp => new ModsViewModel(
            sp.GetRequiredService<ModCatalogStore>(),
            sp.GetRequiredService<CatalogSync>().GetVersionsAsync,
            sp.GetRequiredService<ModResolutionService>(),
            sp.GetRequiredService<ModInstallService>(),
            sp.GetRequiredService<GameProfiles>(),
            sp.GetRequiredService<DashboardViewModel>(),
            sp.GetRequiredService<ModLogos>(),
            sp.GetRequiredService<FavouritesStore>(),
            sp.GetRequiredService<ILogger<ModsViewModel>>(),
            sp.GetRequiredService<SettingsStore>()));
        services.AddSingleton<Func<UpdatesViewModel>>(sp => () => new UpdatesViewModel(
            sp.GetRequiredService<DashboardViewModel>(),
            sp.GetRequiredService<ModResolutionService>(),
            sp.GetRequiredService<GameProfiles>(),
            sp.GetRequiredService<ModCatalogStore>(),
            sp.GetRequiredService<CatalogSync>().GetVersionsAsync,
            sp.GetRequiredService<ILogger<UpdatesViewModel>>()));
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static HttpClient Http(IServiceProvider sp, string name) =>
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(name);

    private static void Configure(HttpClient http, TimeSpan timeout)
    {
        http.Timeout = timeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd(FicsitApi.USER_AGENT);
    }
}
