using System.ComponentModel;
using ADA.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ADA.Views;

public partial class MainWindow : Window
{
    private DashboardViewModel? _dashboard;
    private CreateProfileViewModel? _shownPrompt;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Watches the Dashboard for a create-profile prompt, which can open from any page.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_dashboard is not null)
        {
            _dashboard.PropertyChanged -= OnDashboardChanged;
        }
        _dashboard = (DataContext as MainViewModel)?.Dashboard;
        if (_dashboard is not null)
        {
            _dashboard.PropertyChanged += OnDashboardChanged;
            ShowProfilePrompt();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ShowProfilePrompt();
    }

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.ProfilePrompt))
        {
            ShowProfilePrompt();
        }
    }

    /// <summary>
    /// Opens the Dashboard's prompt as a dialog once the window is visible (a dialog needs a shown
    /// owner). Each prompt object opens at most one dialog; it clears ProfilePrompt when it closes.
    /// </summary>
    private async void ShowProfilePrompt()
    {
        if (!IsVisible || _dashboard?.ProfilePrompt is not { } prompt || ReferenceEquals(prompt, _shownPrompt))
        {
            return;
        }
        _shownPrompt = prompt;
        await new CreateProfileWindow { DataContext = prompt }.ShowDialog(this);
    }

    // Opens the updates window over this one; it lists updates as soon as it has checked.
    private async void OnCheckForUpdates(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel main)
        {
            return;
        }

        var updates = main.CreateUpdates();
        var window = new UpdatesWindow { DataContext = updates };
        _ = updates.CheckAsync();
        await window.ShowDialog(this);
    }
}
