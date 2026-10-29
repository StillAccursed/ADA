using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADA.ViewModels;

/// <summary>A yes/no question with an optional checkbox. <see cref="Answer"/> completes when it closes.</summary>
public partial class ConfirmViewModel(string title, string message, string confirmText, string cancelText = "Cancel") : ViewModelBase
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised once when the dialog should close.</summary>
    public event EventHandler? Closed;

    public string Title { get; } = title;

    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    public string CancelText { get; } = cancelText;

    /// <summary>Colours the confirm button red, for actions that delete something.</summary>
    public bool IsDanger { get; init; }

    /// <summary>The checkbox's text; null shows no checkbox.</summary>
    public string? OptionText { get; init; }

    /// <summary>Why the checkbox can't be ticked; null when it can.</summary>
    public string? OptionUnavailableReason { get; init; }

    public bool HasOption => OptionText is not null;

    public bool CanChooseOption => OptionUnavailableReason is null;

    [ObservableProperty]
    public partial bool OptionChecked { get; set; }

    /// <summary>True for confirm, false for cancel or closing the window.</summary>
    public Task<bool> Answer => _answer.Task;

    [RelayCommand]
    private void Confirm() => Close(true);

    /// <summary>Also called by the window when it's closed another way, so <see cref="Answer"/> always completes.</summary>
    [RelayCommand]
    internal void Cancel() => Close(false);

    private void Close(bool confirmed)
    {
        if (_answer.TrySetResult(confirmed))
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
