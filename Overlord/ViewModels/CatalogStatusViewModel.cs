using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Overlord.Infrastructure.Catalog;

namespace Overlord.ViewModels;

/// <summary>
/// State of the local mod catalogue: how many mods it holds and when it last checked ficsit.app.
/// Refreshed on launch and then periodically. A refresh only learns about new releases; it never
/// installs or updates a mod, which is always the user's choice.
/// </summary>
public partial class CatalogStatusViewModel(CatalogSync sync, ILogger<CatalogStatusViewModel> log) : ViewModelBase
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

    /// <summary>Shows what is already on disk straight away, without waiting for the network. Never throws.</summary>
    public async Task LoadAsync()
    {
        try
        {
            (ModCount, LastChecked) = await Task.Run(() => (sync.ModCount, sync.LastChecked));
        }
        catch (Exception e)
        {
            log.LogError(e, "Couldn't read the mod catalogue");
            ErrorText = $"Couldn't read the saved mod list: {e.Message}";
        }
    }

    /// <summary>Refreshes now, then every <paramref name="interval"/> for as long as the app runs.</summary>
    public async Task KeepFreshAsync(TimeSpan interval, CancellationToken ct = default)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            // Skip a tick while one is still running (e.g. a slow first download).
            if (!IsChecking)
                await RefreshCommand.ExecuteAsync(null);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsChecking = true;
        ErrorText = null;
        try
        {
            await Task.Run(() => sync.SyncAsync());
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Catalogue check failed");
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
