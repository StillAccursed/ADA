using System.ComponentModel;
using ADA.Imaging;
using ADA.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;

namespace ADA.Views;

public partial class ModsView : UserControl
{
    private ModsViewModel? _viewModel;
    private bool _restoringOffset;
    private int _restoreGeneration;

    public ModsView()
    {
        InitializeComponent();
        // Descriptions remain selectable without introducing author-supplied navigation or
        // shell actions. The dedicated ficsit.app button below remains the external link.
        DescriptionViewer.Plugins.HyperlinkCommand = new RelayCommand(static () => { }, static () => false);
        DescriptionViewer.Plugins.PathResolver = new ModDescriptionImageResolver(null);
        DescriptionViewer.Plugins = DescriptionViewer.Plugins;
        ResultsList.AddHandler(ScrollViewer.ScrollChangedEvent, OnResultsScrollChanged);
    }

    /// <summary>Changes the description image loader with the view model; other contexts cannot resolve images.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        DescriptionViewer.Plugins.PathResolver = (DataContext as ModsViewModel)?.DescriptionImageResolver
            ?? new ModDescriptionImageResolver(null);
        // Reassigning applies the plugin configuration to the engine and rerenders any bound description.
        DescriptionViewer.Plugins = DescriptionViewer.Plugins;
    }

    /// <summary>Refreshes installed state while retaining this session's page, loaded results and scroll offset.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is ModsViewModel vm)
        {
            _viewModel = vm;
            vm.PropertyChanged += OnBrowsingChanged;
            _ = vm.RefreshAsync();
        }
    }

    /// <summary>Detaches view callbacks; the shared view model keeps its browsing position for the next visit.</summary>
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_viewModel is { } vm)
        {
            vm.PropertyChanged -= OnBrowsingChanged;
        }
        _viewModel = null;
        _restoringOffset = true;
        _restoreGeneration++;
        base.OnUnloaded(e);
    }

    private void OnBrowsingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is not { HasSelection: false } vm)
        {
            return;
        }
        if (e.PropertyName is nameof(ModsViewModel.ResultsRevision) or nameof(ModsViewModel.HasSelection)
            || (e.PropertyName == nameof(ModsViewModel.IsSearching) && !vm.IsSearching))
        {
            RestoreScrollOffset(vm);
        }
    }

    /// <summary>Waits for layout before restoring; collapse and collection-reset events must not overwrite the saved offset.</summary>
    private void RestoreScrollOffset(ModsViewModel vm)
    {
        _restoringOffset = true;
        var generation = ++_restoreGeneration;
        var offset = vm.ResultsScrollOffset;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _restoreGeneration || !ReferenceEquals(vm, _viewModel) || vm.HasSelection)
            {
                return;
            }
            ResultsList.UpdateLayout();
            var scroll = ResultsList.FindDescendantOfType<ScrollViewer>();
            if (scroll is not null)
            {
                scroll.Offset = new Vector(0, offset);
                vm.ResultsScrollOffset = scroll.Offset.Y;
            }
            _restoringOffset = false;
            TryLoadMore(vm, scroll);
        }, DispatcherPriority.Loaded);
    }

    private void OnResultsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_restoringOffset || _viewModel is not { HasSelection: false, IsSearching: false, IsUpdatingResults: false } vm
            || e.Source is not ScrollViewer scroll || !ReferenceEquals(scroll, ResultsList.FindDescendantOfType<ScrollViewer>()))
        {
            return;
        }
        vm.ResultsScrollOffset = scroll.Offset.Y;
        TryLoadMore(vm, scroll);
    }

    /// <summary>Prefetches within one viewport of the bottom and fills a viewport that is taller than the first batch.</summary>
    private static void TryLoadMore(ModsViewModel vm, ScrollViewer? scroll)
    {
        if (!vm.IsSearching && scroll is { Viewport.Height: > 0 } && vm.LoadMoreCommand.CanExecute(null)
            && scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height <= scroll.Viewport.Height)
        {
            vm.LoadMoreCommand.Execute(null);
        }
    }

    /// <summary>Opens the selected mod's reference-based ficsit.app address in the user's browser.</summary>
    private async void OnOpenOnFicsit(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ModsViewModel { SelectedMod: { } mod } && TopLevel.GetTopLevel(this) is { } top)
        {
            await top.Launcher.LaunchUriAsync(mod.FicsitUri);
        }
    }
}
