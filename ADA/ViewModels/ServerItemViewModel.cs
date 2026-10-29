using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Servers;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ADA.ViewModels;

/// <summary>One saved server on the Servers page, with its live connection state.</summary>
public partial class ServerItemViewModel(ServerConnection server) : ViewModelBase
{
    public ServerConnection Server { get; private set; } = server;

    public string Name => Server.Name;

    /// <summary>Where it is, as shown in lists: <c>host:port</c>, or a folder server's folder.</summary>
    public string Endpoint => Server.Transport == ServerTransport.Folder ? Server.GameRoot : $"{Server.Address}:{Server.Port}";

    public string SystemText => Server.Host == GameHost.Windows ? "Windows server" : "Linux server";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(StatusText), nameof(ConnectText), nameof(IsUnavailable))]
    public partial ServerState State { get; private set; }

    /// <summary>Why it's unavailable, or the last connect failure; shown under the status.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; internal set; }

    /// <summary>The game build, once connected.</summary>
    [ObservableProperty]
    public partial string? BuildText { get; private set; }

    /// <summary>Another PC's hold on this server's mods, e.g. "Being changed from GAMING-PC since 14:02"; null when there's none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    public partial string? LockText { get; internal set; }

    public bool IsLocked => LockText is not null;

    /// <summary>Connecting, disconnecting or removing is in progress for this server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectText))]
    public partial bool IsBusy { get; internal set; }

    /// <summary>Connected, or left connected and currently unreachable: either way Disconnect is offered.</summary>
    public bool IsConnected => State is ServerState.Connected or ServerState.Unavailable or ServerState.Connecting;

    public bool IsUnavailable => State == ServerState.Unavailable;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    public string StatusText => State switch
    {
        ServerState.Connected => "Connected",
        ServerState.Connecting => "Connecting…",
        ServerState.Unavailable => "Unavailable",
        _ => "Not connected",
    };

    public string ConnectText => IsBusy && !IsConnected ? "Connecting…" : IsConnected ? "Disconnect" : "Connect";

    /// <summary>Takes the saved details and this server's live state from the connection manager.</summary>
    internal void Update(ServerConnection saved, ServerConnections connections)
    {
        Server = saved;
        var live = connections.Server?.Id == saved.Id;
        State = live ? connections.State : ServerState.Disconnected;
        if (live)
        {
            Problem = connections.Problem;
        }
        BuildText = live ? connections.Install?.BuildText : null;
        if (!live || State != ServerState.Connected)
        {
            LockText = null;
        }
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(SystemText));
    }
}
