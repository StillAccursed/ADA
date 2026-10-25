using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overlord.Core.Games;

namespace Overlord.ViewModels;

public partial class DashboardViewModel(IGameDetector detector, CatalogStatusViewModel catalog) : ViewModelBase
{
    public CatalogStatusViewModel Catalog { get; } = catalog;

    public ObservableCollection<GameInstall> Installs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsScanning { get; set; }

    public string StatusText => IsScanning
        ? "Looking for Satisfactory…"
        : Installs.Count switch
        {
            0 => "No Satisfactory install found. Overlord only manages copies installed through Steam or the Epic Games Launcher.",
            1 => "1 install found.",
            var n => $"{n} installs found.",
        };

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        try
        {
            // Detection reads registry hives and walks folders; keep it off the UI thread.
            var found = await Task.Run(detector.Detect);
            Installs.Clear();
            foreach (var install in found)
                Installs.Add(install);
        }
        finally
        {
            IsScanning = false;
        }
    }
}
