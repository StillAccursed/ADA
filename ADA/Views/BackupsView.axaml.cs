using System.ComponentModel;
using ADA.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ADA.Views;

/// <summary>
/// The Backups page. Re-reads the list each time it's shown, shows the page's questions as dialogs over the
/// main window (each at most once), opens the backup folder, and runs the folder picker for choosing a new one.
/// </summary>
public partial class BackupsView : UserControl
{
    private BackupsViewModel? _page;
    private ConfirmViewModel? _shown;

    public BackupsView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _page = DataContext as BackupsViewModel;
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
        if (e.PropertyName != nameof(BackupsViewModel.Confirmation)
            || _page?.Confirmation is not { } question
            || ReferenceEquals(question, _shown)
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        _shown = question;
        await new ConfirmWindow { DataContext = question }.ShowDialog(owner);
    }

    private async void OnChangeFolder(object? sender, RoutedEventArgs e)
    {
        if (_page is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var start = await storage.TryGetFolderFromPathAsync(_page.Schedule.Folder);
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where to keep backups",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            _page.Schedule.ChangeFolder(path);
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
