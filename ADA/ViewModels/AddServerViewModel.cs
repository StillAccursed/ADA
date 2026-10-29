using System.Collections.ObjectModel;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADA.ViewModels;

/// <summary>One Test Connection check as shown on the Add Server page.</summary>
public sealed class SetupStepViewModel(SetupStep step)
{
    public string Name => step.Name;

    public string Detail => step.Detail;

    public string Glyph => step.Passed ? "✓" : step.Blocking ? "✕" : "!";

    public bool Passed => step.Passed;

    public bool IsWarning => !step.Passed && !step.Blocking;
}

/// <summary>
/// The Add Server page. Add is offered only once Test Connection has passed with exactly the details
/// shown and the user has trusted the server's identity; any edit afterwards means testing again.
/// </summary>
/// <param name="test">Runs Test Connection; <see cref="ServerSetupCheck.RunAsync"/> in the app, a fake in tests.</param>
/// <param name="add">Saves the server and its secret, then reports whether the secret lasts beyond this session.</param>
public partial class AddServerViewModel(
    Func<ServerConnection, string?, IProgress<SetupStep>, CancellationToken, Task<SetupResult>> test,
    Func<ServerConnection, string, Task<SecretPersistence>> add,
    Func<ServerConnection, string?> problemWith) : ViewModelBase
{
    // One id for the life of the page, so the secret saved on Add is filed under the id the test used.
    private readonly Guid _id = Guid.NewGuid();
    private SetupResult? _result;
    private ServerConnection? _tested;
    private CancellationTokenSource? _testing;

    /// <summary>Raised with the saved server once it's added, and with null when the user goes back.</summary>
    public event EventHandler<ServerConnection?>? Finished;

    public ObservableCollection<SetupStepViewModel> Steps { get; } = [];

    /// <summary>
    /// How the server is reached. A folder on this PC or a mounted network share needs no login, and
    /// its address is optional (only for telling whether the server is running). FTP always uses TLS
    /// and a password. Changing between SFTP and FTP moves the port to the other's default when it
    /// was left at one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(UsesSftp), nameof(UsesFtp), nameof(UsesFolder), nameof(UsesLogin))]
    [NotifyPropertyChangedFor(nameof(UsesKeyFile), nameof(SecretLabel), nameof(PortLabel))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial ServerTransport Transport { get; set; }

    // Settable for the connection-type radio buttons: ticking one selects its transport.
    public bool UsesSftp
    {
        get => Transport == ServerTransport.Sftp;
        set => SelectTransport(value, ServerTransport.Sftp);
    }

    public bool UsesFtp
    {
        get => Transport == ServerTransport.Ftp;
        set => SelectTransport(value, ServerTransport.Ftp);
    }

    public bool UsesFolder
    {
        get => Transport == ServerTransport.Folder;
        set => SelectTransport(value, ServerTransport.Folder);
    }

    /// <summary>SFTP and FTP log in with an address, port and user; a folder doesn't.</summary>
    public bool UsesLogin => Transport != ServerTransport.Folder;

    public string PortLabel => UsesFtp ? "FTP port" : "SFTP port";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial decimal? Port { get; set; } = ServerConnection.DEFAULT_SSH_PORT;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string User { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd), nameof(SecretLabel), nameof(UsesKeyFile))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool UsesKey { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string Secret { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string KeyPath { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string GameRoot { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial decimal? ApiPort { get; set; } = 7777;

    /// <summary>The user has checked the fingerprint and trusts this server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool TrustsIdentity { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(AddCommand))]
    public partial bool IsTesting { get; private set; }

    /// <summary>Why the details can't be tested or added; shown above the buttons.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    public string SecretLabel => UsesKeyFile ? "Key passphrase (if it has one)" : "Password";

    /// <summary>An SSH key is chosen; only SFTP offers one.</summary>
    public bool UsesKeyFile => UsesSftp && UsesKey;

    /// <summary>Shown once a test has reached the server, for the user to trust.</summary>
    public string? Fingerprint => _result?.HostKeyFingerprint is { } f ? $"SHA256:{f}" : null;

    /// <summary>
    /// The details tested are the ones shown, the test passed and found the server's OS, and for SFTP
    /// the user trusts the server's identity (a folder server has none).
    /// </summary>
    public bool CanAdd => !IsTesting && _result is { Passed: true } && _tested == Draft()
                          && _result.Host is not null && (UsesFolder || TrustsIdentity);

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        var draft = Draft();
        if (problemWith(draft) is { } problem)
        {
            Problem = problem;
            return;
        }

        _testing?.Cancel();
        _testing = new CancellationTokenSource();
        Problem = null;
        Steps.Clear();
        _result = null;
        TrustsIdentity = false;
        IsTesting = true;
        try
        {
            var progress = new Progress<SetupStep>(step => Steps.Add(new SetupStepViewModel(step)));
            _result = await test(draft, Secret.Length == 0 ? null : Secret, progress, _testing.Token);
            _tested = draft;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(Fingerprint));
            OnPropertyChanged(nameof(CanAdd));
            AddCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        var server = Draft() with
        {
            GameRoot = _result!.GameRoot!,
            Host = _result.Host!.Value,
            HostKeyFingerprint = _result.HostKeyFingerprint ?? "",
        };
        try
        {
            var persistence = await add(server, UsesFolder ? "" : Secret);
            if (persistence == SecretPersistence.ThisSessionOnly)
            {
                Problem = "This computer has no keyring, so the password is kept only until ADA closes.";
            }
            Finished?.Invoke(this, server);
        }
        catch (InvalidServerException e)
        {
            Problem = e.Message;
        }
    }

    [RelayCommand]
    private void Back()
    {
        _testing?.Cancel();
        Finished?.Invoke(this, null);
    }

    private bool CanTest => !IsTesting;

    /// <summary>
    /// The details as entered. Equal drafts mean nothing has changed since the test. The OS is a
    /// placeholder; Add fills in what the test found. The secret isn't part of a draft, so editing it
    /// clears the test separately. A folder server carries no login details, so editing hidden SFTP
    /// fields doesn't count as a change.
    /// </summary>
    private ServerConnection Draft() => Transport switch
    {
        ServerTransport.Folder => new(_id, Name.Trim(), Address.Trim(), ServerConnection.DEFAULT_SSH_PORT, "", ServerAuth.Password,
            GameRoot.Trim(), GameHost.Linux, "", null, (int)(ApiPort ?? 0), Transport: ServerTransport.Folder),
        ServerTransport.Ftp => new(_id, Name.Trim(), Address.Trim(), (int)(Port ?? 0), User.Trim(), ServerAuth.Password,
            GameRoot.Trim(), GameHost.Linux, "", null, (int)(ApiPort ?? 0), Transport: ServerTransport.Ftp),
        _ => new(_id, Name.Trim(), Address.Trim(), (int)(Port ?? 0), User.Trim(),
            UsesKey ? ServerAuth.PrivateKey : ServerAuth.Password, GameRoot.Trim(), GameHost.Linux, "",
            UsesKey ? KeyPath.Trim() : null, (int)(ApiPort ?? 0)),
    };

    private void SelectTransport(bool selected, ServerTransport transport)
    {
        if (selected)
        {
            Transport = transport;
        }
    }

    partial void OnTransportChanged(ServerTransport oldValue, ServerTransport newValue)
    {
        Port = (oldValue, newValue, Port) switch
        {
            (ServerTransport.Sftp, ServerTransport.Ftp, ServerConnection.DEFAULT_SSH_PORT) => ServerConnection.DEFAULT_FTP_PORT,
            (ServerTransport.Ftp, ServerTransport.Sftp, ServerConnection.DEFAULT_FTP_PORT) => ServerConnection.DEFAULT_SSH_PORT,
            _ => Port,
        };
    }

    partial void OnSecretChanged(string value) => _tested = null;
}
