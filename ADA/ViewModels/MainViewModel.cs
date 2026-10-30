using System.ComponentModel;
using ADA.Infrastructure.Servers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ADA.ViewModels;

public partial class NavItem(string title, string glyph, ViewModelBase? page) : ObservableObject
{
    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    /// <summary>The destination page, or null for a non-selectable category heading.</summary>
    public ViewModelBase? Page { get; } = page;

    /// <summary>Identifies category headings, which cannot receive navigation selection.</summary>
    public bool IsHeader => Page is null;

    /// <summary>Shows an update arrow at the end of the button.</summary>
    [ObservableProperty]
    public partial bool HasUpdates { get; set; }
}

public partial class MainViewModel : ViewModelBase
{
    private readonly DashboardViewModel _dashboard;
    private readonly NavItem _dashboardNav;
    private readonly NavItem _modsNav;
    private readonly Func<UpdatesViewModel> _updates;
    private readonly ServerConnections _connections;

    public IReadOnlyList<NavItem> NavItems { get; }

    // Often enough that new releases show up during a session, gentle on ficsit.app.
    private static readonly TimeSpan CATALOG_REFRESH_INTERVAL = TimeSpan.FromMinutes(30);

    /// <summary>The Dashboard page; the main window also shows its create-profile prompt over any page.</summary>
    internal DashboardViewModel Dashboard => _dashboard;

    /// <summary>The mod catalogue; its last update time is shown under "Check for Updates".</summary>
    public CatalogStatusViewModel Catalog { get; }

    [ObservableProperty]
    public partial NavItem SelectedNav { get; set; }

    public MainViewModel(DashboardViewModel dashboard, ModsViewModel mods, ServersViewModel servers, SavesViewModel saves,
        ServerConnections connections, CatalogStatusViewModel catalog, SettingsViewModel settings, Func<UpdatesViewModel> updates)
    {
        _dashboard = dashboard;
        _updates = updates;
        _connections = connections;
        Catalog = catalog;
        _dashboardNav = new NavItem("Dashboard", "◧", dashboard);
        _modsNav = new NavItem("Mods", "▦", mods);
        NavItems =
        [
            _dashboardNav,

            new("Satisfactory", "", null),
            _modsNav,
            new("Servers", "⇅", servers),

            new("Installations", "", null),
            new("Saves", "▤", saves),
            new("Backups", "▣", new PlaceholderViewModel("Backups", "Coming soon")),

            new("Misc", "", null),
            new("Activity", "◷", new PlaceholderViewModel("Activity", "Install transaction history and logs. Coming in Phase 1.")),
            new("Settings", "⚙", settings),
        ];
        SelectedNav = _dashboardNav;

        // A save's "Install missing mods": queue them on the Mods page and show it for review.
        saves.InstallRequested += (_, references) =>
        {
            mods.QueueForInstall(references);
            SelectedNav = _modsNav;
        };

        dashboard.PropertyChanged += OnDashboardChanged;
        catalog.PropertyChanged += OnCatalogChanged;
    }

    /// <summary>
    /// On launch: find installs, reconnect the server the user left connected, show the cached
    /// catalogue at once, then fetch the latest mods.
    /// </summary>
    public void Start()
    {
        _dashboard.AttachUiContext();
        _dashboard.ScanCommand.Execute(null);
        _ = _dashboard.WatchGameAsync();
        _ = _connections.ResumeAsync();
        _ = Catalog.LoadAsync();
        _ = Catalog.KeepFreshAsync(CATALOG_REFRESH_INTERVAL);
    }

    /// <summary>A fresh "Check for Updates" window's contents.</summary>
    public UpdatesViewModel CreateUpdates() => _updates();

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.HasUpdates))
        {
            _dashboardNav.HasUpdates = _dashboard.HasUpdates;
        }
    }

    // New releases arrive with each catalogue refresh: look for updates (never install them).
    private void OnCatalogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogStatusViewModel.IsChecking) && !Catalog.IsChecking)
        {
            _ = _dashboard.FindUpdatesAsync();
        }
    }
}
