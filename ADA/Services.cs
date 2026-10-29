using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Secrets;
using ADA.Imaging;
using ADA.Infrastructure.Catalog;
using ADA.Infrastructure.Downloads;
using ADA.Infrastructure.Ficsit;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Servers;
using ADA.Infrastructure.Settings;
using ADA.Platform.Detection;
using ADA.Platform.Secrets;
using ADA.Platform.Storage;
using ADA.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace ADA;

/// <summary>The composition root: every long-lived service and page, built once at startup.</summary>
public static class Services
{
    private const string API_CLIENT = "ficsit-api";
    private const string DOWNLOAD_CLIENT = "ficsit-downloads";
    private const string SERVER_API_CLIENT = "server-api";

    /// <summary>
    /// Builds the container and validates it immediately, so a missing registration fails at startup
    /// rather than on first use.
    /// </summary>
    public static ServiceProvider Build(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(logging => logging.AddSerilog(dispose: true));

        // API queries are small and should fail fast. Downloads can be many GB, so they have no
        // total limit; ArchiveDownloader instead retries a connection that stops sending data.
        services.AddHttpClient(API_CLIENT, http => Configure(http, TimeSpan.FromSeconds(30)));
        services.AddHttpClient(DOWNLOAD_CLIENT, http => Configure(http, Timeout.InfiniteTimeSpan));
        // Only asks whether a server is up, so a short wait. Dedicated servers use self-signed
        // certificates; see SatisfactoryServerApi for why accepting them is safe for this client only.
        services.AddHttpClient(SERVER_API_CLIENT, http => http.Timeout = TimeSpan.FromSeconds(3))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });

        services.AddSingleton(sp => new FicsitApiClient(Http(sp, API_CLIENT)));
        services.AddSingleton(sp => new ModCatalogStore(paths.CatalogDatabase));
        services.AddSingleton(sp => new CatalogSync(sp.GetRequiredService<FicsitApiClient>(), sp.GetRequiredService<ModCatalogStore>(),
            log: sp.GetRequiredService<ILogger<CatalogSync>>()));
        services.AddSingleton<IModVersionSource>(sp => sp.GetRequiredService<CatalogSync>());
        services.AddSingleton(sp => new SettingsStore(paths.SettingsFile));
        services.AddSingleton(_ => new FavouritesStore(paths.FavouritesFile));
        services.AddSingleton(sp => new ArchiveCache(paths.Archives));
        services.AddSingleton(_ => new DashboardStorageReader(paths.Archives));
        services.AddSingleton(sp => new ArchiveDownloader(Http(sp, DOWNLOAD_CLIENT), paths.Archives,
            log: sp.GetRequiredService<ILogger<ArchiveDownloader>>()));
        services.AddSingleton(sp => new ModInstallService(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ArchiveDownloader>(),
            sp.GetRequiredService<IModVersionSource>(),
            sp.GetRequiredService<GameRunningChecks>().IsRunningAsync,
            sp.GetRequiredService<ILogger<ModInstallService>>(),
            sp.GetRequiredService<GameFilesFactory>()));
        services.AddSingleton<ModResolutionService>();
        services.AddSingleton(_ => new ProfileStore(paths.Profiles, paths.ProfilesState));
        services.AddSingleton(sp => new GameProfiles(
            sp.GetRequiredService<ProfileStore>(),
            sp.GetRequiredService<ModInstallService>(),
            sp.GetRequiredService<ILogger<GameProfiles>>()));
        services.AddSingleton(sp => new ProfileSync(sp.GetRequiredService<ProfileStore>(), sp.GetRequiredService<ModInstallService>(),
            sp.GetRequiredService<IModVersionSource>()));
        services.AddSingleton(_ => GameDetection.ForCurrentOS());
        services.AddSingleton(_ => SecretStores.ForCurrentOS());
        services.AddSingleton(sp => new ServerSecrets(sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<ILogger<ServerSecrets>>()));
        services.AddSingleton(sp => new ServerStore(paths.ServersFile, sp.GetRequiredService<ILogger<ServerStore>>()));
        services.AddSingleton(sp => new SatisfactoryServerApi(Http(sp, SERVER_API_CLIENT)));
        services.AddSingleton(sp => new ServerConnections(sp.GetRequiredService<ServerStore>(), sp.GetRequiredService<ServerSecrets>(),
            sp.GetRequiredService<SatisfactoryServerApi>(), sp.GetRequiredService<ILogger<ServerConnections>>()));
        services.AddSingleton(sp => new ServerSetupCheck(sp.GetRequiredService<ServerConnections>(), sp.GetRequiredService<SatisfactoryServerApi>()));
        // Any local Satisfactory process blocks every local install: ps can't say which install it runs from.
        services.AddSingleton(sp => new GameRunningChecks(sp.GetRequiredService<ServerConnections>(), GameProcesses.IsAnyRunningAsync));
        services.AddSingleton(sp => new GameFilesFactory(sp.GetRequiredService<ServerConnections>()));
        services.AddSingleton(sp => new ConfigPush(sp.GetRequiredService<ModInstallService>(), sp.GetRequiredService<GameFilesFactory>(),
            sp.GetRequiredService<GameRunningChecks>().IsRunningAsync, sp.GetRequiredService<ILogger<ConfigPush>>()));
        services.AddSingleton(sp => new ModLogos(Http(sp, API_CLIENT), sp.GetRequiredService<ILogger<ModLogos>>()));

        services.AddSingleton<CatalogStatusViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<Func<GameInstall, GameInstallViewModel>>(sp => install => new GameInstallViewModel(
            install, sp.GetRequiredService<ModInstallService>(), sp.GetRequiredService<ModResolutionService>(),
            sp.GetRequiredService<GameProfiles>(),
            sp.GetRequiredService<ILogger<GameInstallViewModel>>()));
        services.AddSingleton(sp => new ModChangelogs(sp.GetRequiredService<IModVersionSource>()));
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ModsViewModel>();
        services.AddSingleton<ServersViewModel>();
        services.AddSingleton<Func<UpdatesViewModel>>(sp => () => ActivatorUtilities.CreateInstance<UpdatesViewModel>(sp));
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
