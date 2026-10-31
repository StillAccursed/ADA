using System.ComponentModel;
using ADA.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ADA.Views;

public partial class MainWindow : Window
{
    private DashboardViewModel? _dashboard;
    private CreateProfileViewModel? _shownPrompt;
    private SwitchProfileViewModel? _shownSwitch;
    private ConfirmViewModel? _shownSyncQuestion;
    private ConfirmViewModel? _shownDeleteProfileQuestion;
    private PushConfigsViewModel? _shownConfigs;

    public MainWindow()
    {
        InitializeComponent();
        InterfaceScale.Fit(this);
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
        else if (e.PropertyName == nameof(DashboardViewModel.SwitchPrompt))
        {
            ShowSwitchPrompt();
        }
        else if (e.PropertyName == nameof(DashboardViewModel.SyncQuestion))
        {
            ShowSyncQuestion();
        }
        else if (e.PropertyName == nameof(DashboardViewModel.ConfigsPrompt))
        {
            ShowConfigsPrompt();
        }
        else if (e.PropertyName == nameof(DashboardViewModel.DeleteProfileQuestion))
        {
            ShowDeleteProfileQuestion();
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

    /// <summary>Opens the Dashboard's switch preview as a dialog; each prompt object opens at most one.</summary>
    private async void ShowSwitchPrompt()
    {
        if (!IsVisible || _dashboard?.SwitchPrompt is not { } prompt || ReferenceEquals(prompt, _shownSwitch))
        {
            return;
        }
        _shownSwitch = prompt;
        await new SwitchProfileWindow { DataContext = prompt }.ShowDialog(this);
    }

    /// <summary>Opens the Dashboard's "Also apply to the server?" question as a dialog; each question opens at most once.</summary>
    private async void ShowSyncQuestion()
    {
        if (!IsVisible || _dashboard?.SyncQuestion is not { } question || ReferenceEquals(question, _shownSyncQuestion))
        {
            return;
        }
        _shownSyncQuestion = question;
        await new ConfirmWindow { DataContext = question }.ShowDialog(this);
    }

    /// <summary>Opens the Dashboard's Push configs dialog; each one opens at most once.</summary>
    private async void ShowConfigsPrompt()
    {
        if (!IsVisible || _dashboard?.ConfigsPrompt is not { } prompt || ReferenceEquals(prompt, _shownConfigs))
        {
            return;
        }
        _shownConfigs = prompt;
        await new PushConfigsWindow { DataContext = prompt }.ShowDialog(this);
    }

    /// <summary>Shows each profile deletion question once, using the shared destructive-action confirmation.</summary>
    private async void ShowDeleteProfileQuestion()
    {
        if (!IsVisible || _dashboard?.DeleteProfileQuestion is not { } question || ReferenceEquals(question, _shownDeleteProfileQuestion))
        {
            return;
        }
        _shownDeleteProfileQuestion = question;
        await new ConfirmWindow { DataContext = question }.ShowDialog(this);
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
