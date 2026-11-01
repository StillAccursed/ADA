using System.ComponentModel;
using ADA.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ADA.Views;

/// <summary>
/// The Activity page. Reads the history the first time it's shown, shows Clear history's question as a dialog over
/// the main window (at most once), and opens the history's folder.
/// </summary>
public partial class ActivityView : UserControl
{
    private ActivityViewModel? _page;
    private ConfirmViewModel? _shown;

    public ActivityView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _page = DataContext as ActivityViewModel;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPageChanged;
            _ = _page.ShowAsync();
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPageChanged;
        }
        _page = null;
        base.OnUnloaded(e);
    }

    private async void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActivityViewModel.Confirmation)
            || _page?.Confirmation is not { } question
            || ReferenceEquals(question, _shown)
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        _shown = question;
        await new ConfirmWindow { DataContext = question }.ShowDialog(owner);
    }

    private async void OnShowFolder(object? sender, RoutedEventArgs e)
    {
        if (_page is not null && TopLevel.GetTopLevel(this) is { } top)
        {
            Directory.CreateDirectory(_page.FolderPath);
            await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(_page.FolderPath));
        }
    }
}
