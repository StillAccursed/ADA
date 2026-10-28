using ADA.ViewModels;
using Avalonia.Controls;

namespace ADA.Views;

/// <summary>Shows a <see cref="SwitchProfileViewModel"/>; closes when the dialog reports it is done.</summary>
public partial class SwitchProfileWindow : Window
{
    private SwitchProfileViewModel? _prompt;

    public SwitchProfileWindow()
    {
        InitializeComponent();
        InterfaceScale.Fit(this);
    }

    /// <summary>Follows the dialog's lifetime; the window never outlives it.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_prompt is not null)
        {
            _prompt.Closed -= OnPromptClosed;
        }
        _prompt = DataContext as SwitchProfileViewModel;
        if (_prompt is not null)
        {
            _prompt.Closed += OnPromptClosed;
        }
    }

    /// <summary>
    /// The title-bar close button counts as Cancel. Closing is refused while the switch is being
    /// worked out or applied, so its result is never lost behind a closed window.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_prompt is { IsWorking: true })
        {
            e.Cancel = true;
        }
        else if (_prompt is { } prompt)
        {
            _prompt = null;
            prompt.Closed -= OnPromptClosed;
            prompt.CancelCommand.Execute(null);
        }
    }

    private void OnPromptClosed(object? sender, bool switched)
    {
        if (_prompt is { } prompt)
        {
            _prompt = null;
            prompt.Closed -= OnPromptClosed;
        }
        Close();
    }
}
