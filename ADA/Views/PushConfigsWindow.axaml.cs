using ADA.ViewModels;
using Avalonia.Controls;

namespace ADA.Views;

/// <summary>Shows a <see cref="PushConfigsViewModel"/>; closes when it does, and closing it closes the dialog.</summary>
public partial class PushConfigsWindow : Window
{
    private PushConfigsViewModel? _dialog;

    public PushConfigsWindow()
    {
        InitializeComponent();
        InterfaceScale.Fit(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_dialog is not null)
        {
            _dialog.Closed -= OnDialogClosed;
        }
        _dialog = DataContext as PushConfigsViewModel;
        if (_dialog is not null)
        {
            _dialog.Closed += OnDialogClosed;
        }
    }

    /// <summary>The title-bar close button counts as Close, unless a push is still running.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_dialog is not { } dialog)
        {
            return;
        }
        if (dialog.IsWorking)
        {
            e.Cancel = true;
            return;
        }
        _dialog = null;
        dialog.Closed -= OnDialogClosed;
        dialog.CloseCommand.Execute(null);
    }

    private void OnDialogClosed(object? sender, bool changed)
    {
        if (_dialog is { } dialog)
        {
            _dialog = null;
            dialog.Closed -= OnDialogClosed;
            Close();
        }
    }
}
