using Avalonia.Controls;
using Avalonia.Interactivity;
using ADA.ViewModels;

namespace ADA.Views;

public partial class UpdatesWindow : Window
{
    public UpdatesWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as UpdatesViewModel)?.Close();
    }
}
