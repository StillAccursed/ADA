using CommunityToolkit.Mvvm.ComponentModel;

namespace Overlord.ViewModels;

public sealed record NavItem(string Title, string Glyph, ViewModelBase Page);

public partial class MainViewModel : ViewModelBase
{
    private readonly DashboardViewModel _dashboard;
    private readonly CatalogStatusViewModel _catalog;

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    public partial NavItem SelectedNav { get; set; }

    public MainViewModel(DashboardViewModel dashboard, CatalogStatusViewModel catalog, SettingsViewModel settings)
    {
        _dashboard = dashboard;
        _catalog = catalog;
        NavItems =
        [
            new("Dashboard", "◧", dashboard),
            new("Mods", "▦", new PlaceholderViewModel("Mods", "Browse, search and install mods. Coming in Phase 2.")),
            new("Profiles", "☰", new PlaceholderViewModel("Profiles", "Switch, import and export mod profiles. Coming in Phase 2.")),
            new("Servers", "⇅", new PlaceholderViewModel("Servers", "Deploy profiles to dedicated servers over FTP and SFTP. Coming in Phase 3.")),
            new("Activity", "◷", new PlaceholderViewModel("Activity", "Install transaction history and logs. Coming in Phase 1.")),
            new("Settings", "⚙", settings),
        ];
        SelectedNav = NavItems[0];
    }

    /// <summary>On launch: find installs, show the cached catalogue at once, then fetch the latest mods.</summary>
    public void Start()
    {
        _dashboard.ScanCommand.Execute(null);
        _ = _dashboard.WatchGameAsync();
        _ = _catalog.LoadAsync();
        _catalog.CheckForUpdatesCommand.Execute(null);
    }
}
