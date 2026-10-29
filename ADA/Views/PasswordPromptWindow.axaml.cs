using ADA.ViewModels;
using Avalonia.Controls;

namespace ADA.Views;

/// <summary>Shows a <see cref="PasswordPromptViewModel"/>; closes when it has an answer.</summary>
public partial class PasswordPromptWindow : Window
{
    private PasswordPromptViewModel? _prompt;

    public PasswordPromptWindow()
    {
        InitializeComponent();
        InterfaceScale.Fit(this);
    }

    /// <summary>Follows the prompt's lifetime; the window never outlives it.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_prompt is not null)
        {
            _prompt.Closed -= OnPromptClosed;
        }
        _prompt = DataContext as PasswordPromptViewModel;
        if (_prompt is not null)
        {
            _prompt.Closed += OnPromptClosed;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        SecretBox.Focus();
    }

    /// <summary>The title-bar close button counts as Cancel.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_prompt is { } prompt)
        {
            _prompt = null;
            prompt.Closed -= OnPromptClosed;
            prompt.Cancel();
        }
    }

    private void OnPromptClosed(object? sender, EventArgs e)
    {
        if (_prompt is { } prompt)
        {
            _prompt = null;
            prompt.Closed -= OnPromptClosed;
            Close();
        }
    }
}
