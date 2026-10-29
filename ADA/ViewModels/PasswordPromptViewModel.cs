using ADA.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADA.ViewModels;

/// <summary>
/// Asks for a server's password or key passphrase when none is saved. <see cref="Answer"/> is the
/// text entered, or null if the user cancelled.
/// </summary>
public partial class PasswordPromptViewModel(ServerConnection server) : ViewModelBase
{
    private readonly TaskCompletionSource<string?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler? Closed;

    public string Title => $"Connect to {server.Name}";

    public string Label => server.Auth == ServerAuth.Password ? "Password" : "Key passphrase";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial string Secret { get; set; } = "";

    /// <summary>The secret entered, or null if the user cancelled.</summary>
    public Task<string?> Answer => _answer.Task;

    [RelayCommand(CanExecute = nameof(HasSecret))]
    private void Connect() => Close(Secret);

    [RelayCommand]
    internal void Cancel() => Close(null);

    private bool HasSecret => Secret.Length > 0;

    private void Close(string? secret)
    {
        if (_answer.TrySetResult(secret))
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }
}
