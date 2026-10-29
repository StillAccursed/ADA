using System.Collections.ObjectModel;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Servers;
using ADA.Infrastructure.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>
/// The Servers page: saved servers in the same layouts as Mods, the Add Server page in their place,
/// and connecting, disconnecting and removing. Store and connection events can arrive on any
/// thread; they're posted to the page's thread before anything bound changes.
/// </summary>
public partial class ServersViewModel : ViewModelBase
{
    private readonly ServerStore _store;
    private readonly ServerSecrets _secrets;
    private readonly ServerConnections _connections;
    private readonly ServerSetupCheck _setup;
    private readonly ModInstallService _installs;
    private readonly GameProfiles _profiles;
    private readonly SettingsStore _settings;
    private readonly DashboardViewModel? _dashboard;
    private readonly ILogger _log;
    private SynchronizationContext? _viewContext;

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

    /// <summary>The servers matching <see cref="SearchText"/>.</summary>
    public ObservableCollection<ServerItemViewModel> Shown { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView), nameof(IsGridView), nameof(IsCardView))]
    public partial ModView View { get; set; } = ModView.List;

    public bool IsListView { get => View == ModView.List; set { if (value) { View = ModView.List; } } }

    public bool IsGridView { get => View == ModView.Grid; set { if (value) { View = ModView.Grid; } } }

    public bool IsCardView { get => View == ModView.Card; set { if (value) { View = ModView.Card; } } }

    /// <summary>The Add Server page, shown in place of the list; null while the list shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdding), nameof(ShowsEmptyState), nameof(ShowsList))]
    public partial AddServerViewModel? Adding { get; private set; }

    /// <summary>A question for the page's view to show as a dialog.</summary>
    [ObservableProperty]
    public partial ConfirmViewModel? Confirmation { get; private set; }

    /// <summary>A password request for the page's view to show as a dialog.</summary>
    [ObservableProperty]
    public partial PasswordPromptViewModel? PasswordPrompt { get; private set; }

    /// <summary>The last action's failure, shown above the list.</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    public bool IsAdding => Adding is not null;

    public bool ShowsEmptyState => !IsAdding && Servers.Count == 0;

    public bool ShowsList => !IsAdding && Servers.Count > 0;

    /// <param name="dashboard">Selects a server the user chose to switch to; null where there's no sidebar (tests).</param>
    public ServersViewModel(ServerStore store, ServerSecrets secrets, ServerConnections connections, ServerSetupCheck setup,
        ModInstallService installs, GameProfiles profiles, SettingsStore settings, ILogger<ServersViewModel> log,
        DashboardViewModel? dashboard = null)
    {
        (_store, _secrets, _connections, _setup, _installs, _profiles, _settings, _log, _dashboard) =
            (store, secrets, connections, setup, installs, profiles, settings, log, dashboard);
        View = Enum.TryParse<ModView>(settings.Current.ServersView, out var saved) ? saved : ModView.List;
        store.Changed += OnSourceChanged;
        connections.Changed += OnSourceChanged;
        Reload();
    }

    /// <summary>Called by the view each time it's shown; events from other threads are posted here, and the lock is re-read.</summary>
    internal void AttachViewContext()
    {
        _viewContext ??= SynchronizationContext.Current;
        _ = RefreshLockAsync();
    }

    /// <summary>Shows who holds the connected server's lock, if it's another ADA. Never throws.</summary>
    internal async Task RefreshLockAsync()
    {
        if (_connections is not { State: ServerState.Connected, Server: { } server }
            || Servers.FirstOrDefault(i => i.Server.Id == server.Id) is not { } item)
        {
            return;
        }
        var holder = await _connections.LockHolderAsync();
        item.LockText = holder switch
        {
            null => null,
            { IsUnknown: true } => "Left locked by an unfinished change",
            _ => $"Being changed from {holder.Machine} since {holder.Since.ToLocalTime():g}",
        };
    }

    [RelayCommand]
    private void AddServer()
    {
        var page = new AddServerViewModel(
            (draft, secret, progress, ct) => _setup.RunAsync(draft, secret, progress, ct,
                _dashboard?.Installs.Where(i => !i.Install.IsRemote).Select(i => i.Install.GamePath).ToList()),
            AddAsync,
            _store.ProblemWith);
        page.Finished += OnAddFinished;
        Adding = page;
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync(ServerItemViewModel item)
    {
        Notice = null;
        item.IsBusy = true;
        try
        {
            if (item.IsConnected)
            {
                await _connections.DisconnectAsync();
            }
            else
            {
                await ConnectAsync(item.Server);
            }
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    /// <summary>Removes another PC's lock once the user confirms that PC has stopped.</summary>
    [RelayCommand]
    private async Task TakeOverLockAsync(ServerItemViewModel item)
    {
        var holder = item.LockText;
        if (holder is null || !await AskAsync(new ConfirmViewModel($"Take over {item.Name}?",
                $"{holder}. Only take over if that PC has stopped or crashed. If it's still changing mods, both PCs' changes could be lost.",
                "Take over") { IsDanger = true }))
        {
            return;
        }
        Notice = null;
        try
        {
            await _connections.BreakLockAsync();
        }
        catch (ServerUnavailableException e)
        {
            Notice = e.Message;
        }
        await RefreshLockAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync(ServerItemViewModel item)
    {
        var connected = _connections.Server?.Id == item.Server.Id && _connections.State == ServerState.Connected;
        var question = new ConfirmViewModel($"Remove {item.Name}?",
            "ADA forgets this server and its saved password. Its saved profiles are kept.", "Remove")
        {
            IsDanger = true,
            OptionText = "Also uninstall the mods ADA installed on this server",
            OptionUnavailableReason = connected ? null : "Connect to the server first to uninstall its mods.",
        };
        if (!await AskAsync(question))
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            if (question.OptionChecked && _connections.Install is { } install)
            {
                var installed = await Task.Run(() => _installs.InstallerFor(install).Installed.Keys.ToList());
                if (installed.Count > 0)
                {
                    await _installs.UninstallAsync(install, installed);
                }
                await _profiles.ForgetAsync(install);
            }
            else
            {
                await _profiles.ForgetAsync(new(ServerConnections.InstallKey(item.Server.Id), GameLauncher.Unknown,
                    item.Server.Host, GameKind.DedicatedServer, null, item.Name));
            }

            if (_connections.Server?.Id == item.Server.Id)
            {
                await _connections.DisconnectAsync();
            }
            await _secrets.DeleteAsync(item.Server.Id);
            _store.Remove(item.Server.Id);
        }
        catch (Exception e) when (e is InstallException or IOException)
        {
            // Nothing is forgotten if uninstalling failed, so the user can try again.
            Notice = $"{item.Name} wasn't removed: {e.Message}";
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnViewChanged(ModView value) => _ = Task.Run(() =>
    {
        try
        {
            _settings.Update(s => s with { ServersView = value.ToString() });
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Couldn't save the Servers page's layout");
        }
    });

    private async Task<SecretPersistence> AddAsync(ServerConnection server, string secret)
    {
        _store.Save(server);
        return secret.Length == 0 ? SecretPersistence.Saved : await _secrets.SaveAsync(server, secret);
    }

    private async void OnAddFinished(object? sender, ServerConnection? added)
    {
        Adding = null;
        if (added is not null && await AskAsync(new ConfirmViewModel($"Connect to {added.Name} and switch to it?",
                "ADA connects to one server at a time; any other server is disconnected first. You can switch back from the sidebar.",
                "Connect and switch", "Not now")) && await ConnectAsync(added))
        {
            _dashboard?.SelectInstall(ServerConnections.InstallKey(added.Id));
        }
    }

    /// <summary>Connects, asking for the password when none is saved. Failures show as the page's notice.</summary>
    /// <returns>Whether the server is now connected.</returns>
    private async Task<bool> ConnectAsync(ServerConnection server)
    {
        try
        {
            await _connections.ConnectAsync(server.Id);
            await RefreshLockAsync();
            return true;
        }
        catch (ServerConnectException e) when (e.NeedsSecret)
        {
            var prompt = new PasswordPromptViewModel(server);
            PasswordPrompt = prompt;
            var secret = await prompt.Answer;
            PasswordPrompt = null;
            if (secret is null)
            {
                return false;
            }
            await _secrets.SaveAsync(server, secret);
            return await TryConnectAsync(server);
        }
        catch (ServerConnectException e)
        {
            Notice = e.Message;
            return false;
        }
    }

    private async Task<bool> TryConnectAsync(ServerConnection server)
    {
        try
        {
            await _connections.ConnectAsync(server.Id);
            await RefreshLockAsync();
            return true;
        }
        catch (ServerConnectException e)
        {
            Notice = e.Message;
            return false;
        }
    }

    private async Task<bool> AskAsync(ConfirmViewModel question)
    {
        Confirmation = question;
        try
        {
            return await question.Answer;
        }
        finally
        {
            Confirmation = null;
        }
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        if (_viewContext is { } context && !ReferenceEquals(context, SynchronizationContext.Current))
        {
            context.Post(_ => Reload(), null);
        }
        else
        {
            Reload();
        }
    }

    /// <summary>Brings the list in line with the saved servers and the live connection, keeping item objects where it can.</summary>
    private void Reload()
    {
        var saved = _store.All;
        foreach (var gone in Servers.Where(i => saved.All(s => s.Id != i.Server.Id)).ToList())
        {
            Servers.Remove(gone);
        }
        for (var i = 0; i < saved.Count; i++)
        {
            var item = Servers.FirstOrDefault(s => s.Server.Id == saved[i].Id) ?? new ServerItemViewModel(saved[i]);
            item.Update(saved[i], _connections);
            if (!Servers.Contains(item))
            {
                Servers.Insert(i, item);
            }
            else if (Servers.IndexOf(item) != i)
            {
                Servers.Move(Servers.IndexOf(item), i);
            }
        }
        OnPropertyChanged(nameof(ShowsEmptyState));
        OnPropertyChanged(nameof(ShowsList));
        Filter();
    }

    private void Filter()
    {
        var query = SearchText.Trim();
        Shown.Clear();
        foreach (var item in Servers.Where(i => query.Length == 0
                     || i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                     || i.Endpoint.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            Shown.Add(item);
        }
    }
}
