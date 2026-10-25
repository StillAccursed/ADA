using System;
using System.Collections.Generic;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using Overlord.Infrastructure.Catalog;
using Overlord.Infrastructure.Ficsit;
using Overlord.Infrastructure.Settings;
using Overlord.Platform.Detection;
using Overlord.Platform.Storage;

namespace Overlord.ViewModels;

public sealed record NavItem(string Title, string Glyph, ViewModelBase Page);

public partial class MainViewModel : ViewModelBase
{
    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    public partial NavItem SelectedNav { get; set; }

    public MainViewModel()
    {
        var paths = AppPaths.ForCurrentOS().EnsureCreated();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var sync = new CatalogSync(new FicsitApiClient(http), new ModCatalogStore(paths.CatalogDatabase));
        var catalog = new CatalogStatusViewModel(sync);

        var dashboard = new DashboardViewModel(GameDetection.ForCurrentOS(), catalog);
        NavItems =
        [
            new("Dashboard", "◧", dashboard),
            new("Mods", "▦", new PlaceholderViewModel("Mods", "Browse, search and install mods. Coming in Phase 2.")),
            new("Profiles", "☰", new PlaceholderViewModel("Profiles", "Switch, import and export mod profiles. Coming in Phase 2.")),
            new("Servers", "⇅", new PlaceholderViewModel("Servers", "Deploy profiles to dedicated servers over FTP and SFTP. Coming in Phase 3.")),
            new("Activity", "◷", new PlaceholderViewModel("Activity", "Install transaction history and logs. Coming in Phase 1.")),
            new("Settings", "⚙", new SettingsViewModel(new SettingsStore(paths.SettingsFile), new ArchiveCache(paths.Archives))),
        ];
        SelectedNav = NavItems[0];

        // On launch: find installs, show the cached catalogue at once, then fetch the latest mods.
        dashboard.ScanCommand.Execute(null);
        _ = catalog.LoadAsync();
        catalog.CheckForUpdatesCommand.Execute(null);
    }
}
