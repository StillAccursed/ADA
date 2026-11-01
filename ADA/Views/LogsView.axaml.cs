using System.ComponentModel;
using ADA.ViewModels;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ADA.Views;

/// <summary>
/// The Logs tab. Avalonia builds a tab's content only while it's selected, so this loads each time the tab is
/// opened, and re-reads the log then. Scrolls to the newest message after each read, puts Copy's text on the
/// clipboard, and opens the logs folder.
/// </summary>
public partial class LogsView : UserControl
{
    private LogsViewModel? _page;

    public LogsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _page = DataContext as LogsViewModel;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPageChanged;
            _page.CopyRequested += OnCopyRequested;
            _ = _page.ShowAsync();
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPageChanged;
            _page.CopyRequested -= OnCopyRequested;
        }
        _page = null;
        base.OnUnloaded(e);
    }

    /// <summary>After a new list, shows its end: the newest messages are the ones being looked for.</summary>
    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsViewModel.Shown) && _page?.Shown is { Count: > 0 } shown)
        {
            Dispatcher.UIThread.Post(() => Lines.ScrollIntoView(shown[^1]), DispatcherPriority.Background);
        }
    }

    private async void OnCopyRequested(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
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
