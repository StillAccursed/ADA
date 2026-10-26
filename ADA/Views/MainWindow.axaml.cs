using Avalonia.Controls;
using Avalonia.Interactivity;
using ADA.ViewModels;

namespace ADA.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Opens the updates window over this one; it lists updates as soon as it has checked.
    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main)
            return;

        var updates = main.CreateUpdates();
        var window = new UpdatesWindow { DataContext = updates };
        _ = updates.CheckAsync();
        await window.ShowDialog(this);
    }
}
