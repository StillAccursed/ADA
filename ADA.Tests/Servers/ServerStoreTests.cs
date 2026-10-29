using System.Text.Json;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Servers;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Servers;

public sealed class ServerStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _path;

    public ServerStoreTests() => _path = _dir.Combine("Config", "servers.json");

    public void Dispose() => _dir.Dispose();

    private static ServerConnection Server(string name, bool connected = false) => new(Guid.NewGuid(), name, "192.168.1.20", 22,
        "steam", ServerAuth.Password, "/home/steam/SatisfactoryDedicatedServer", GameHost.Linux, "SHA256:abc", Connected: connected);

    [Fact]
    public void SavedServersAreListedByNameAndSurviveAReload()
    {
        var store = new ServerStore(_path);
        var b = Server("Bravo");
        var a = Server("alpha");

        store.Save(b);
        store.Save(a);

        Assert.Equal(["alpha", "Bravo"], store.All.Select(s => s.Name));
        Assert.Equal([a, b], new ServerStore(_path).All);
    }

    [Fact]
    public void SavingTheSameIdReplacesTheServer()
    {
        var store = new ServerStore(_path);
        var server = Server("Home");
        store.Save(server);

        store.Save(server with { Address = "10.0.0.5" });

        Assert.Equal("10.0.0.5", Assert.Single(store.All).Address);
    }

    [Fact]
    public void NamesAddressAndUserAreTrimmed()
    {
        var store = new ServerStore(_path);

        store.Save(Server("  Home  ") with { Address = " host ", User = " steam " });

        var saved = Assert.Single(store.All);
        Assert.Equal(("Home", "host", "steam"), (saved.Name, saved.Address, saved.User));
    }

    [Fact]
    public void RemoveDeletesOnlyThatServer()
    {
        var store = new ServerStore(_path);
        var keep = Server("Keep");
        var drop = Server("Drop");
        store.Save(keep);
        store.Save(drop);

        Assert.True(store.Remove(drop.Id));
        Assert.False(store.Remove(drop.Id));

        Assert.Equal([keep], new ServerStore(_path).All);
    }

    [Fact]
    public void NamesMustBeUniqueIgnoringCase()
    {
        var store = new ServerStore(_path);
        store.Save(Server("Home"));

        var e = Assert.Throws<InvalidServerException>(() => store.Save(Server("HOME")));

        Assert.Equal("A server called HOME already exists.", e.Message);
    }

    [Theory]
    [InlineData("", "host", 22, "steam", "/srv", "Give the server a name.")]
    [InlineData("Home", " ", 22, "steam", "/srv", "Enter the server's address.")]
    [InlineData("Home", "host", 0, "steam", "/srv", "Ports must be between 1 and 65535.")]
    [InlineData("Home", "host", 70000, "steam", "/srv", "Ports must be between 1 and 65535.")]
    [InlineData("Home", "host", 22, "", "/srv", "Enter the user name to log in with.")]
    [InlineData("Home", "host", 22, "steam", "", "Enter the server folder.")]
    public void IncompleteServersAreRefusedWithAReason(string name, string address, int port, string user, string root, string problem)
    {
        var store = new ServerStore(_path);
        var server = Server(name) with { Address = address, Port = port, User = user, GameRoot = root };

        Assert.Equal(problem, store.ProblemWith(server));
        Assert.Throws<InvalidServerException>(() => store.Save(server));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void KeyLoginNeedsAKeyFileAndLongNamesAreRefused()
    {
        var store = new ServerStore(_path);

        Assert.Equal("Choose the private key file.", store.ProblemWith(Server("Home") with { Auth = ServerAuth.PrivateKey }));
        Assert.Null(store.ProblemWith(Server("Home") with { Auth = ServerAuth.PrivateKey, PrivateKeyPath = "~/.ssh/id_ed25519" }));
        Assert.Equal($"The name can be at most {ServerStore.MAX_NAME_LENGTH} characters.",
            store.ProblemWith(Server(new string('x', ServerStore.MAX_NAME_LENGTH + 1))));
    }

    [Fact]
    public void OnlyOneServerIsConnectedAtATime()
    {
        var store = new ServerStore(_path);
        var a = Server("A", connected: true);
        var b = Server("B");
        store.Save(a);
        store.Save(b);

        store.SetConnected(b.Id);
        Assert.Equal(b.Id, new ServerStore(_path).Connected?.Id);

        store.Save(store.Find(a.Id)! with { Connected = true });
        Assert.Equal([a.Id], store.All.Where(s => s.Connected).Select(s => s.Id));

        store.SetConnected(null);
        Assert.Null(store.Connected);
    }

    [Fact]
    public void ChangedIsRaisedOnlyWhenSomethingChanged()
    {
        var store = new ServerStore(_path);
        var server = Server("Home");
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Save(server);
        store.SetConnected(server.Id);
        store.SetConnected(server.Id);
        store.Remove(Guid.NewGuid());
        store.Remove(server.Id);

        Assert.Equal(3, raised);
    }

    [Fact]
    public void ADamagedFileIsMovedAsideNotOverwritten()
    {
        _dir.WriteFile("Config/servers.json", "{ not json");
        var store = new ServerStore(_path);

        Assert.Empty(store.All);
        store.Save(Server("New"));

        var aside = Assert.Single(Directory.GetFiles(_dir.Combine("Config"), "servers.json.damaged-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
    }

    [Fact]
    public void TheFileHoldsConnectionDetailsOnly()
    {
        var store = new ServerStore(_path);
        store.Save(Server("Home") with { Auth = ServerAuth.PrivateKey, PrivateKeyPath = "~/.ssh/id_ed25519" });

        using var json = JsonDocument.Parse(File.ReadAllText(_path));
        var properties = json.RootElement[0].EnumerateObject().Select(p => p.Name).Order().ToArray();

        Assert.Equal(["address", "apiPort", "auth", "connected", "gameRoot", "host", "hostKeyFingerprint", "id", "name",
            "port", "privateKeyPath", "transport", "user"], properties);
    }

    [Fact]
    public void An_ftp_server_logs_in_with_a_password_only()
    {
        using var dir = new TempDir();
        var store = new ServerStore(dir.Combine("servers.json"));
        var ftp = new ServerConnection(Guid.NewGuid(), "Host", "ftp.example", 21, "steam", ServerAuth.Password, "/srv", GameHost.Linux, "abc",
            Transport: ServerTransport.Ftp);

        Assert.Null(store.ProblemWith(ftp));
        Assert.Equal("FTP servers log in with a password.",
            store.ProblemWith(ftp with { Auth = ServerAuth.PrivateKey, PrivateKeyPath = "/k" }));
    }
}
