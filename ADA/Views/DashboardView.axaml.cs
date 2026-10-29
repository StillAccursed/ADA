using System.ComponentModel;
using ADA.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ADA.Views;

public partial class DashboardView : UserControl
{
    private DashboardViewModel? _viewModel;
    private GameInstallViewModel? _game;
    private int _layoutMode = -1;

    public DashboardView()
    {
        InitializeComponent();
    }

    /// <summary>Refreshes data on entry and attaches view-only callbacks.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is DashboardViewModel vm)
        {
            _viewModel = vm;
            vm.PropertyChanged += OnDashboardChanged;
            WatchInstall(vm.SelectedInstall);
            _ = vm.RefreshCardsAsync();
        }
        ArrangeCards();
    }

    /// <summary>Detaches callbacks while the shared dashboard retains its state.</summary>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_viewModel is { } vm)
        {
            vm.PropertyChanged -= OnDashboardChanged;
        }
        WatchInstall(null);
        _viewModel = null;
        base.OnUnloaded(e);
    }

    /// <summary>Reflows whole cards when the available dashboard width crosses a breakpoint.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == BoundsProperty || change.Property == FontSizeProperty) && CardGrid is not null)
        {
            ArrangeCards();
        }
    }

    private void OnDashboardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.SelectedInstall))
        {
            WatchInstall(_viewModel?.SelectedInstall);
        }
    }

    /// <summary>Follows the selected installation's own changes, including progress the Dashboard doesn't forward.</summary>
    private void WatchInstall(GameInstallViewModel? game)
    {
        if (_game is not null)
        {
            _game.PropertyChanged -= OnInstallChanged;
        }
        _game = game;
        if (game is not null)
        {
            game.PropertyChanged += OnInstallChanged;
        }
        UpdateAttentionCard();
    }

    private void OnInstallChanged(object? sender, PropertyChangedEventArgs e) => UpdateAttentionCard();

    /// <summary>
    /// Shows the attention card while the selected installation is working, has a result or problem
    /// to report, or offers found-mod import, fix or repair actions; hides it otherwise.
    /// </summary>
    private void UpdateAttentionCard()
    {
        AttentionCard.IsVisible = _game is { } game
            && ((game.IsBusy && !string.IsNullOrEmpty(game.ProgressText))
                || !string.IsNullOrEmpty(game.Notice)
                || game.HasFoundMods || game.HasFix || game.HasFixProblem || game.HasRepairs);
    }

    private void ArrangeCards()
    {
        var width = (Bounds.Width - 48) / Math.Max(1, FontSize / 14);
        var mode = width >= 800 ? 3 : width >= 560 ? 2 : 1;
        if (_layoutMode == mode)
        {
            return;
        }
        _layoutMode = mode;
        CardGrid.ColumnDefinitions = ColumnDefinitions.Parse(mode == 3 ? "*,1.18*,0.95*" : mode == 2 ? "*,1.18*" : "*");
        Grid.SetColumn(LatestCard, mode >= 2 ? 1 : 0);
        Grid.SetRow(LatestCard, mode >= 2 ? 0 : 1);
        Grid.SetRowSpan(LatestCard, mode == 2 ? 2 : 1);
        Grid.SetColumn(RightCards, mode == 3 ? 2 : 0);
        Grid.SetRow(RightCards, mode == 3 ? 0 : mode == 2 ? 1 : 2);
    }
}
