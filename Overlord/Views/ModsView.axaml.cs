using Avalonia.Controls;
using Avalonia.Interactivity;
using Overlord.ViewModels;

namespace Overlord.Views;

public partial class ModsView : UserControl
{
    public ModsView()
    {
        InitializeComponent();
    }

    // Mods change from the Dashboard (repairs) and Profiles too, so re-read them each time the page opens.
    // RefreshAsync reports its own errors, so nothing here can throw into the UI thread.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is ModsViewModel vm)
            _ = vm.RefreshAsync();
    }
}
