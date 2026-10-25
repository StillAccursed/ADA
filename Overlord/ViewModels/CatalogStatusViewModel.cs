using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Overlord.Infrastructure.Catalog;

namespace Overlord.ViewModels;

/// <summary>
/// State of the local mod catalogue: how many mods it holds, when it last checked ficsit.app,
/// and the "Check for Updates" action. Checked once on launch, then whenever the user asks.
/// </summary>
public partial class CatalogStatusViewModel(CatalogSync sync) : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial int ModCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial DateTimeOffset? LastChecked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    public string SummaryText => (IsChecking, LastChecked) switch
    {
        (true, null) => "Downloading the mod list from ficsit.app…",
        (true, _) => "Checking for updates…",
        (false, null) => "Mod list not downloaded yet.",
        (false, { } at) => $"{ModCount.ToString("N0", CultureInfo.CurrentCulture)} mods · Mods updated {FormatWhen(at)}",
    };

    /// <summary>Shows what is already on disk straight away, without waiting for the network.</summary>
    public async Task LoadAsync()
    {
        (ModCount, LastChecked) = await Task.Run(() => (sync.ModCount, sync.LastChecked));
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsChecking = true;
        ErrorText = null;
        try
        {
            await Task.Run(() => sync.SyncAsync());
        }
        catch (Exception e)
        {
            ErrorText = LastChecked is null
                ? $"Couldn't download the mod list: {e.Message}"
                : $"Couldn't check for updates: {e.Message} Showing the mod list from {FormatWhen(LastChecked.Value)}.";
        }
        finally
        {
            await LoadAsync();
            IsChecking = false;
        }
    }

    private static string FormatWhen(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        return local.Date == DateTime.Today
            ? $"today at {time}"
            : $"{local.ToString("d MMM yyyy", CultureInfo.CurrentCulture)} at {time}";
    }
}
