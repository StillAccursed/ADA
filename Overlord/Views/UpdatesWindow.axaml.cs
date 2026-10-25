using Avalonia.Controls;
using Avalonia.Interactivity;
using Overlord.ViewModels;

namespace Overlord.Views;

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
