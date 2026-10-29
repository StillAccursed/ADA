using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Servers;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Servers;

public sealed class ServerSecretsTests
{
    private static readonly ServerConnection SERVER = new(Guid.NewGuid(), "My Server", "192.168.1.20", 22, "steam",
        ServerAuth.Password, "/home/steam/SatisfactoryDedicatedServer", GameHost.Linux, "SHA256:abc");

    [Fact]
    public async Task SavesToTheOsStoreWhenThereIsOne()
    {
        var store = new FakeSecretStore();
        var secrets = new ServerSecrets(store);

        Assert.Equal(SecretPersistence.Saved, await secrets.SaveAsync(SERVER, "hunter2"));

        Assert.Equal("hunter2", store.Saved[$"server:{SERVER.Id:N}"]);
        Assert.Equal("hunter2", await secrets.LoadAsync(SERVER.Id));
    }

    [Fact]
    public async Task KeepsTheSecretForThisSessionOnlyWithoutAnOsStore()
    {
        var store = new FakeSecretStore { Unavailable = true };
        var secrets = new ServerSecrets(store);

        Assert.Equal(SecretPersistence.ThisSessionOnly, await secrets.SaveAsync(SERVER, "hunter2"));

        Assert.Empty(store.Saved);
        Assert.Equal("hunter2", await secrets.LoadAsync(SERVER.Id));
        Assert.Null(await new ServerSecrets(store).LoadAsync(SERVER.Id));
    }

    [Fact]
    public async Task ASavedSecretReplacesTheSessionCopy()
    {
        var store = new FakeSecretStore { Unavailable = true };
        var secrets = new ServerSecrets(store);
        await secrets.SaveAsync(SERVER, "old");

        store.Unavailable = false;
        await secrets.SaveAsync(SERVER, "new");
        store.Saved[$"server:{SERVER.Id:N}"] = "changed elsewhere";

        Assert.Equal("changed elsewhere", await secrets.LoadAsync(SERVER.Id));
    }

    [Fact]
    public async Task DeleteClearsTheOsStoreAndTheSessionCopy()
    {
        var store = new FakeSecretStore { Unavailable = true };
        var secrets = new ServerSecrets(store);
        await secrets.SaveAsync(SERVER, "session");
        store.Unavailable = false;
        store.Saved[$"server:{SERVER.Id:N}"] = "saved";

        await secrets.DeleteAsync(SERVER.Id);

        Assert.Empty(store.Saved);
        Assert.Null(await secrets.LoadAsync(SERVER.Id));
    }

    [Fact]
    public async Task AnUnreadableStoreMeansTheUserIsAsked()
    {
        var secrets = new ServerSecrets(new FakeSecretStore { Unavailable = true });

        Assert.Null(await secrets.LoadAsync(SERVER.Id));
        await secrets.DeleteAsync(SERVER.Id);
    }
}
