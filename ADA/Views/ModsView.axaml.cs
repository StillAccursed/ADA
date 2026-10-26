using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;
using ADA.ViewModels;

namespace ADA.Views;

public partial class ModsView : UserControl
{
    public ModsView()
    {
        InitializeComponent();
        // Descriptions remain selectable without introducing author-supplied navigation or
        // shell actions. The dedicated ficsit.app button below remains the external link.
        DescriptionViewer.Plugins.HyperlinkCommand = new RelayCommand(static () => { }, static () => false);
    }

    // Mods change from the Dashboard (repairs) and Profiles too, so re-read them each time the page opens.
    // RefreshAsync reports its own errors, so nothing here can throw into the UI thread.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is ModsViewModel vm)
            _ = vm.RefreshAsync();
    }

    // Opens the mod's ficsit.app page in the user's browser; the address is built from the reference only.
    private async void OnOpenOnFicsit(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ModsViewModel { SelectedMod: { } mod } && TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(mod.FicsitUri);
    }
}
