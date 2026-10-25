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
    // RefreshAsync reports its own errors, so nothing here can throw into the UI thread.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is SettingsViewModel vm)
            _ = vm.RefreshAsync();
    }
}
