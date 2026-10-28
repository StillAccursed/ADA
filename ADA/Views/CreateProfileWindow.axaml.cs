using ADA.ViewModels;
using Avalonia.Controls;

namespace ADA.Views;

/// <summary>Shows a <see cref="CreateProfileViewModel"/>; closes when the prompt reports it is done.</summary>
public partial class CreateProfileWindow : Window
{
    private CreateProfileViewModel? _prompt;

    public CreateProfileWindow()
    {
        InitializeComponent();
    }

    /// <summary>Follows the prompt's lifetime; the window never outlives it.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_prompt is not null)
        {
            _prompt.Closed -= OnPromptClosed;
        }
        _prompt = DataContext as CreateProfileViewModel;
        if (_prompt is not null)
        {
            _prompt.Closed += OnPromptClosed;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>
    /// The title-bar close button counts as "Not now". Closing is refused while the profile is being
    /// saved, so the result is never lost behind a closed window.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_prompt is { IsCreating: true })
        {
            e.Cancel = true;
        }
        else if (_prompt is { } prompt)
        {
            _prompt = null;
            prompt.Closed -= OnPromptClosed;
            prompt.DismissCommand.Execute(null);
        }
    }

    private void OnPromptClosed(object? sender, bool created)
    {
        if (_prompt is { } prompt)
        {
            _prompt = null;
            prompt.Closed -= OnPromptClosed;
        }
        Close();
    }
}
