using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Overlord.ViewModels;

public partial class NavItem(string title, string glyph, ViewModelBase page) : ObservableObject
{
    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    public ViewModelBase Page { get; } = page;

    /// <summary>Shows an update arrow at the end of the button.</summary>
    [ObservableProperty]
    public partial bool HasUpdates { get; set; }
}

public partial class MainViewModel : ViewModelBase
{
    private readonly DashboardViewModel _dashboard;
    private readonly NavItem _dashboardNav;
    private readonly Func<UpdatesViewModel> _updates;

    public IReadOnlyList<NavItem> NavItems { get; }

    // Often enough that new releases show up during a session, gentle on ficsit.app.
    private static readonly TimeSpan CatalogRefreshInterval = TimeSpan.FromMinutes(30);

    /// <summary>The mod catalogue; its last update time is shown under "Check for Updates".</summary>
    public CatalogStatusViewModel Catalog { get; }

    [ObservableProperty]
    public partial NavItem SelectedNav { get; set; }

    public MainViewModel(DashboardViewModel dashboard, ModsViewModel mods, CatalogStatusViewModel catalog, SettingsViewModel settings,
        Func<UpdatesViewModel> updates)
    {
        _dashboard = dashboard;
        _updates = updates;
        Catalog = catalog;
        _dashboardNav = new NavItem("Dashboard", "◧", dashboard);
        NavItems =
        [
            _dashboardNav,
            new("Mods", "▦", mods),
            new("Profiles", "☰", new PlaceholderViewModel("Profiles", "Switch, import and export mod profiles. Coming in Phase 2.")),
            new("Servers", "⇅", new PlaceholderViewModel("Servers", "Deploy profiles to dedicated servers over FTP and SFTP. Coming in Phase 3.")),
            new("Activity", "◷", new PlaceholderViewModel("Activity", "Install transaction history and logs. Coming in Phase 1.")),
            new("Settings", "⚙", settings),
        ];
        SelectedNav = NavItems[0];

        dashboard.PropertyChanged += OnDashboardChanged;
        catalog.PropertyChanged += OnCatalogChanged;
    }

    /// <summary>On launch: find installs, show the cached catalogue at once, then fetch the latest mods.</summary>
    public void Start()
    {
        _dashboard.ScanCommand.Execute(null);
        _ = _dashboard.WatchGameAsync();
        _ = Catalog.LoadAsync();
        _ = Catalog.KeepFreshAsync(CatalogRefreshInterval);
    }

    /// <summary>A fresh "Check for Updates" window's contents.</summary>
    public UpdatesViewModel CreateUpdates() => _updates();

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.HasUpdates))
            _dashboardNav.HasUpdates = _dashboard.HasUpdates;
    }

    // New releases arrive with each catalogue refresh: look for updates (never install them).
    private void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogStatusViewModel.IsChecking) && !Catalog.IsChecking)
            _ = _dashboard.FindUpdatesAsync();
    }
}
