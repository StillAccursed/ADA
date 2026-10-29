using System.ComponentModel;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADA.ViewModels;

/// <summary>One differing config in the Push configs dialog; ticked by default.</summary>
public partial class ConfigChoiceViewModel(ConfigDifference difference) : ViewModelBase
{
    public string Reference => difference.Reference;

    public string Label => difference.OnServer ? difference.Reference : $"{difference.Reference} (not on the server yet)";

    [ObservableProperty]
    public partial bool IsChosen { get; set; } = true;
}

/// <summary>
/// The Push configs dialog: lists the mod configs that differ between a game and the server sharing
/// its profile, pushes the ticked ones, and can put back what the last push replaced. Never throws;
/// problems are shown in the dialog. Both actions need the server stopped, which it follows live.
/// </summary>
public partial class PushConfigsViewModel : ViewModelBase
{
    private readonly ConfigPush _push;
    private readonly GameInstallViewModel _game;
    private readonly GameInstallViewModel _server;
    private bool _changed;

    /// <summary>Raised once the dialog should close: true when something was pushed or restored.</summary>
    public event EventHandler<bool>? Closed;

    public string Title { get; }

    public string FromText { get; }

    public string Note { get; }

    public IReadOnlyList<ConfigChoiceViewModel> Configs { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushCommand), nameof(RestoreCommand), nameof(CloseCommand))]
    public partial bool IsWorking { get; private set; }

    /// <summary>The last push left the server's previous files, so it can be undone.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool HasBackup { get; private set; }

    /// <summary>What was done, or why it couldn't be.</summary>
    [ObservableProperty]
    public partial string? Outcome { get; private set; }

    /// <summary>"Stop Home first" while the server runs; null otherwise.</summary>
    public string? RunningText => _server.IsGameRunning ? $"Stop {_server.Install.Location} first: a running server reads its configs and can write them back." : null;

    /// <param name="profile">The profile the game and server share, named in the dialog.</param>
    public PushConfigsViewModel(ConfigPush push, GameInstallViewModel game, GameInstallViewModel server, string profile,
        IReadOnlyList<ConfigDifference> differences, bool hasBackup)
    {
        _push = push;
        _game = game;
        _server = server;
        var name = server.Install.Location;
        Title = $"Push mod configs to {name}";
        FromText = $"From {game.Install.DisplayName} · \"{profile}\"";
        Note = $"Restart {name} afterwards for the configs to take effect. Configs some mods keep elsewhere aren't included.";
        Configs = differences.Select(d => new ConfigChoiceViewModel(d)).ToList();
        HasBackup = hasBackup;
        foreach (var config in Configs)
        {
            config.PropertyChanged += (_, _) => PushCommand.NotifyCanExecuteChanged();
        }
        server.PropertyChanged += OnServerChanged;
    }

    private bool CanPush => !IsWorking && !_server.IsGameRunning && Configs.Any(c => c.IsChosen);

    private bool CanRestore => !IsWorking && !_server.IsGameRunning && HasBackup;

    private bool CanClose => !IsWorking;

    [RelayCommand(CanExecute = nameof(CanPush))]
    private Task PushAsync() => RunAsync(async () =>
    {
        var chosen = Configs.Where(c => c.IsChosen).Select(c => c.Reference).ToList();
        await _push.PushAsync(_game.Install, _server.Install, chosen);
        HasBackup = true;
        return $"Pushed {chosen.Count} config{(chosen.Count == 1 ? "" : "s")}. Restart {_server.Install.Location} for them to take effect.";
    });

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreAsync() => RunAsync(async () =>
    {
        await _push.RestoreAsync(_server.Install);
        HasBackup = false;
        return $"Put back {_server.Install.Location}'s previous configs. Configs that were new there stay. Restart it for them to take effect.";
    });

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        _server.PropertyChanged -= OnServerChanged;
        Closed?.Invoke(this, _changed);
    }

    private async Task RunAsync(Func<Task<string>> action)
    {
        IsWorking = true;
        Outcome = null;
        try
        {
            Outcome = await action();
            _changed = true;
        }
        catch (Exception e) when (e is IOException or InstallException or InstallationBusyException or ServerUnavailableException)
        {
            Outcome = e.Message;
        }
        finally
        {
            IsWorking = false;
        }
    }

    private void OnServerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameInstallViewModel.IsGameRunning))
        {
            OnPropertyChanged(nameof(RunningText));
            PushCommand.NotifyCanExecuteChanged();
            RestoreCommand.NotifyCanExecuteChanged();
        }
    }
}
