using ADA.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ADA.Views;

public partial class UpdatesWindow : Window
{
    public UpdatesWindow()
    {
        InitializeComponent();
        InterfaceScale.Fit(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as UpdatesViewModel)?.Close();
    }
}
