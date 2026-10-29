using ADA.Platform.Secrets;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Servers;

public sealed class SecretStoresTests
{
    [SecretStoreFact]
    public async Task RoundTripsThroughThisMachinesSecretStore()
    {
        var store = SecretStores.ForCurrentOS();
        var key = $"test:{Guid.NewGuid():N}";
        try
        {
            Assert.Null(await store.LoadAsync(key));

            await store.SaveAsync(key, "ADA test entry", "first pässword");
            Assert.Equal("first pässword", await store.LoadAsync(key));

            await store.SaveAsync(key, "ADA test entry", "second");
            Assert.Equal("second", await store.LoadAsync(key));

            await store.DeleteAsync(key);
            Assert.Null(await store.LoadAsync(key));
            await store.DeleteAsync(key);
        }
        finally
        {
            await store.DeleteAsync(key);
        }
    }
}
