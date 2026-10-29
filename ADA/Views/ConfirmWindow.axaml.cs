using ADA.ViewModels;
using Avalonia.Controls;

namespace ADA.Views;

/// <summary>Shows a <see cref="ConfirmViewModel"/>; closes when it has an answer.</summary>
public partial class ConfirmWindow : Window
{
    private ConfirmViewModel? _question;

    public ConfirmWindow()
    {
        InitializeComponent();
        InterfaceScale.Fit(this);
    }

    /// <summary>Follows the question's lifetime; the window never outlives it.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_question is not null)
        {
            _question.Closed -= OnQuestionClosed;
        }
        _question = DataContext as ConfirmViewModel;
        if (_question is not null)
        {
            _question.Closed += OnQuestionClosed;
        }
    }

    /// <summary>The title-bar close button counts as Cancel, so the question always gets an answer.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_question is { } question)
        {
            _question = null;
            question.Closed -= OnQuestionClosed;
            question.Cancel();
        }
    }

    private void OnQuestionClosed(object? sender, EventArgs e)
    {
        if (_question is { } question)
        {
            _question = null;
            question.Closed -= OnQuestionClosed;
            Close();
        }
    }
}
