using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Servers;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

public sealed class ServersViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ServerStore _store;
    private readonly FakeSecretStore _secrets = new();
    private readonly SettingsStore _settings;
    private readonly ProfileStore _profileStore;
    private readonly ServerConnections _connections;
    private readonly ModInstallService _installs;
    private readonly ServersViewModel _page;

    public ServersViewModelTests()
    {
        _store = new ServerStore(_dir.Combine("servers.json"));
        _settings = new SettingsStore(_dir.Combine("settings.json"));
        _profileStore = new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
        var api = new SatisfactoryServerApi(new HttpClient(new FakeHttpHandler()));
        var secrets = new ServerSecrets(_secrets);
        _connections = new ServerConnections(_store, secrets, api);
        _installs = new ModInstallService(_settings, new(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
            new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([])), (_, _) => Task.FromResult(false));
        _page = new ServersViewModel(_store, secrets, _connections, new ServerSetupCheck(_connections, api), _installs,
            new GameProfiles(_profileStore, _installs), _settings, NullLogger<ServersViewModel>.Instance);
    }

    public void Dispose()
    {
        _connections.Dispose();
        _dir.Dispose();
    }

    // Port 1 on this machine: nothing listens there, so connecting fails at once.
    private static ServerConnection Server(string name, string address = "127.0.0.1") => new(Guid.NewGuid(), name, address, 1,
        "steam", ServerAuth.Password, "/srv", GameHost.Linux, "abc");

    [Fact]
    public void WithNoServersTheEmptyStateShows()
    {
        Assert.True(_page.ShowsEmptyState);
        Assert.False(_page.ShowsList);
    }

    [Fact]
    public void SavedServersAreListedByNameAndSearchMatchesNamesAndAddresses()
    {
        _store.Save(Server("Bravo", "10.0.0.2"));
        _store.Save(Server("alpha", "192.168.1.20"));

        Assert.Equal(["alpha", "Bravo"], _page.Shown.Select(s => s.Name));
        Assert.True(_page.ShowsList);

        _page.SearchText = "10.0";
        Assert.Equal(["Bravo"], _page.Shown.Select(s => s.Name));
        _page.SearchText = "ALP";
        Assert.Equal(["alpha"], _page.Shown.Select(s => s.Name));
    }

    [Fact]
    public async Task TheLayoutChoiceIsSaved()
    {
        _page.IsCardView = true;

        await Wait.Until(() => _settings.Current.ServersView == "Card");
    }

    [Fact]
    public async Task AFailedConnectShowsWhy()
    {
        var server = Server("Home");
        _store.Save(server);
        _secrets.Saved[$"server:{server.Id:N}"] = "hunter2";

        await _page.ToggleConnectionCommand.ExecuteAsync(_page.Servers[0]);

        Assert.StartsWith("Couldn't reach 127.0.0.1:1", _page.Notice);
        Assert.False(_page.Servers[0].IsConnected);
        Assert.Null(_store.Connected);
    }

    [Fact]
    public async Task AMissingPasswordIsAskedForAndCancellingLeavesItDisconnected()
    {
        _store.Save(Server("Home"));

        var connecting = _page.ToggleConnectionCommand.ExecuteAsync(_page.Servers[0]);
        await Wait.Until(() => _page.PasswordPrompt is not null);
        Assert.Equal("Connect to Home", _page.PasswordPrompt!.Title);
        _page.PasswordPrompt.Cancel();
        await connecting;

        Assert.Null(_page.PasswordPrompt);
        Assert.Empty(_secrets.Saved);
        Assert.Equal(ServerState.Disconnected, _page.Servers[0].State);
    }

    [Fact]
    public async Task RemoveAsksFirstAndCantUninstallWhileDisconnected()
    {
        var server = Server("Home");
        _store.Save(server);
        _secrets.Saved[$"server:{server.Id:N}"] = "hunter2";
        _profileStore.SetActive(ServerConnections.InstallKey(server.Id), "Playthrough");

        var removing = _page.RemoveCommand.ExecuteAsync(_page.Servers[0]);
        await Wait.Until(() => _page.Confirmation is not null);
        var question = _page.Confirmation!;
        Assert.True(question.IsDanger);
        Assert.False(question.CanChooseOption);
        question.ConfirmCommand.Execute(null);
        await removing;

        Assert.Empty(_store.All);
        Assert.Empty(_page.Servers);
        Assert.Empty(_secrets.Saved);
        Assert.Null(_profileStore.ActiveName(ServerConnections.InstallKey(server.Id)));
        Assert.True(_page.ShowsEmptyState);
    }

    [Fact]
    public async Task CancellingRemoveKeepsTheServer()
    {
        _store.Save(Server("Home"));

        var removing = _page.RemoveCommand.ExecuteAsync(_page.Servers[0]);
        await Wait.Until(() => _page.Confirmation is not null);
        _page.Confirmation!.Cancel();
        await removing;

        Assert.Single(_store.All);
    }

    [Fact]
    public void AddingOpensTheAddPageAndBackReturnsToTheList()
    {
        _page.AddServerCommand.Execute(null);
        Assert.True(_page.IsAdding);
        Assert.False(_page.ShowsEmptyState);

        _page.Adding!.BackCommand.Execute(null);

        Assert.False(_page.IsAdding);
        Assert.True(_page.ShowsEmptyState);
    }

    [Fact]
    public async Task TheLockLineIsReReadWhenAnyOperationEnds()
    {
        var root = _dir.Combine("Server");
        Directory.CreateDirectory(Path.Combine(root, "FactoryGame", ".ada"));
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries", "Linux"));
        var server = new ServerConnection(Guid.NewGuid(), "Home", "", 22, "", ServerAuth.Password, root, GameHost.Linux, "",
            Transport: ServerTransport.Folder);
        _store.Save(server);
        await _connections.ConnectAsync(server.Id);
        var item = Assert.Single(_page.Servers);
        Assert.Null(item.LockText);

        File.WriteAllText(Path.Combine(root, "FactoryGame", ".ada", "lock"), System.Text.Json.JsonSerializer.Serialize(
            new LockHolder("OTHER-PC", 1234, DateTimeOffset.UtcNow), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        var game = new GameInstall(_dir.CreateGame("Satisfactory"), GameLauncher.Steam, GameHost.Windows, GameKind.Client, null, "Steam");
        await _installs.RecoverAndCheckAsync(game); // any operation ending raises Changed

        await Wait.Until(() => item.LockText?.Contains("OTHER-PC") == true);
    }
}
