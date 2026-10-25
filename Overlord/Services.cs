using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Overlord.Core.Games;
using Overlord.Infrastructure.Catalog;
using Overlord.Infrastructure.Downloads;
using Overlord.Infrastructure.Ficsit;
using Overlord.Infrastructure.Installing;
using Overlord.Infrastructure.Settings;
using Overlord.Platform.Detection;
using Overlord.Platform.Storage;
using Overlord.ViewModels;
using Serilog;

namespace Overlord;

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
        services.AddSingleton(sp => new ArchiveCache(paths.Archives));
        services.AddSingleton(sp => new ArchiveDownloader(Http(sp, DownloadClient), paths.Archives,
            log: sp.GetRequiredService<ILogger<ArchiveDownloader>>()));
        services.AddSingleton(sp => new ModInstallService(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<ArchiveDownloader>(),
            sp.GetRequiredService<CatalogSync>().GetVersionsAsync,
            GameProcesses.IsAnyRunningAsync,
            sp.GetRequiredService<ILogger<ModInstallService>>()));
        services.AddSingleton(_ => GameDetection.ForCurrentOS());

        services.AddSingleton<CatalogStatusViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<Func<GameInstall, GameInstallViewModel>>(sp => install => new GameInstallViewModel(
            install, sp.GetRequiredService<ModInstallService>(), sp.GetRequiredService<ILogger<GameInstallViewModel>>()));
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static HttpClient Http(IServiceProvider sp, string name) =>
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(name);

    private static void Configure(HttpClient http, TimeSpan timeout)
    {
        http.Timeout = timeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd(FicsitApi.UserAgent);
    }
}
