using Avalonia.Controls;
using Avalonia.Interactivity;
using Overlord.ViewModels;

namespace Overlord.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    // Archive sizes change as mods install, so measure them each time the page is opened.
    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is SettingsViewModel vm)
            await vm.RefreshAsync();
    }
}
