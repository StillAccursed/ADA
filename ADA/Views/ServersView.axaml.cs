using System.ComponentModel;
using ADA.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace ADA.Views;

/// <summary>
/// The Servers page. Opens the page's questions and password requests as dialogs over the main
/// window, each prompt object at most once.
/// </summary>
public partial class ServersView : UserControl
{
    private ServersViewModel? _page;
    private object? _shown;

    public ServersView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPageChanged;
        }
        _page = DataContext as ServersViewModel;
        if (_page is not null)
        {
            _page.PropertyChanged += OnPageChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _page?.AttachViewContext();
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServersViewModel.Confirmation) && _page?.Confirmation is { } question)
        {
            Show(question, () => new ConfirmWindow { DataContext = question });
        }
        else if (e.PropertyName == nameof(ServersViewModel.PasswordPrompt) && _page?.PasswordPrompt is { } prompt)
        {
            Show(prompt, () => new PasswordPromptWindow { DataContext = prompt });
        }
    }

    private async void Show(object prompt, Func<Window> create)
    {
        if (ReferenceEquals(prompt, _shown) || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }
        _shown = prompt;
        await create().ShowDialog(owner);
    }
}
