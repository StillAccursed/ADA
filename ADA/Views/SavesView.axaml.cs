using System.ComponentModel;
using ADA.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ADA.Views;

/// <summary>
/// The Saves page. Tells its view model when it's shown and left, so saves are re-read on every visit and
/// watched only while visible; shows its questions as dialogs over the main window, each at most once; and
/// runs the file pickers for blueprint export and import.
/// </summary>
public partial class SavesView : UserControl
{
    private static readonly FilePickerFileType ZIP = new("Zip archive") { Patterns = ["*.zip"] };

    private static readonly FilePickerFileType BLUEPRINTS = new("Blueprints") { Patterns = ["*.zip", "*.sbp"] };

    private SavesViewModel? _page;
    private ConfirmViewModel? _shown;

    public SavesView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _page = DataContext as SavesViewModel;
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
            _page.Hide();
        }
        _page = null;
        base.OnUnloaded(e);
    }

    private async void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SavesViewModel.Confirmation)
            || _page?.Confirmation is not { } question
            || ReferenceEquals(question, _shown)
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        _shown = question;
        await new ConfirmWindow { DataContext = question }.ShowDialog(owner);
    }

    private async void OnShowInFolder(object? sender, RoutedEventArgs e)
    {
        if (_page?.Save is { } save && TopLevel.GetTopLevel(this) is { } top)
        {
            await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(save.FolderPath));
        }
    }

    private async void OnExportBlueprints(object? sender, RoutedEventArgs e)
    {
        if (_page?.Session is not { } session || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export blueprints",
            SuggestedFileName = $"{session.Name} blueprints.zip",
            DefaultExtension = "zip",
            FileTypeChoices = [ZIP],
        });
        if (file is null)
        {
            return;
        }
        await using var stream = await file.OpenWriteAsync();
        await _page.ExportBlueprintsAsync(stream);
    }

    private async void OnImportBlueprints(object? sender, RoutedEventArgs e)
    {
        if (_page is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import blueprints",
            AllowMultiple = true,
            FileTypeFilter = [BLUEPRINTS],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            await _page.ImportBlueprintsAsync(paths);
        }
    }
}
