using ADA.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ADA.Views;

public partial class AddServerView : UserControl
{
    public AddServerView()
    {
        InitializeComponent();
    }

    // Picks a folder server's folder, which may be on a mounted network share.
    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddServerViewModel page || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the dedicated server's folder",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            page.GameRoot = path;
        }
    }

    // Picks the private key file; only its path is kept, never its contents.
    private async void OnBrowseKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddServerViewModel page || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var ssh = await storage.TryGetFolderFromPathAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"));
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the private key file",
            AllowMultiple = false,
            SuggestedStartLocation = ssh,
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            page.KeyPath = path;
        }
    }
}
