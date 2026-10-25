using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Overlord.Platform.Detection;

namespace Overlord.ViewModels;

public sealed record NavItem(string Title, string Glyph, ViewModelBase Page);

public partial class MainViewModel : ViewModelBase
{
    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty]
    public partial NavItem SelectedNav { get; set; }

    public MainViewModel()
    {
        var dashboard = new DashboardViewModel(GameDetection.ForCurrentOS());
        NavItems =
        [
            new("Dashboard", "◧", dashboard),
            new("Mods", "▦", new PlaceholderViewModel("Mods", "Browse, search and install mods. Coming in Phase 2.")),
            new("Profiles", "☰", new PlaceholderViewModel("Profiles", "Switch, import and export mod profiles. Coming in Phase 2.")),
            new("Servers", "⇅", new PlaceholderViewModel("Servers", "Deploy profiles to dedicated servers over FTP and SFTP. Coming in Phase 3.")),
            new("Activity", "◷", new PlaceholderViewModel("Activity", "Install transaction history and logs. Coming in Phase 1.")),
            new("Settings", "⚙", new PlaceholderViewModel("Settings", "Theme, cache and update settings.")),
        ];
        SelectedNav = NavItems[0];
        dashboard.ScanCommand.Execute(null);
    }
}
