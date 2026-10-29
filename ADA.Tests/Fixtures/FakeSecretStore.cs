using ADA.Core.Secrets;

namespace ADA.Tests.Fixtures;

/// <summary>An in-memory secret store that can pretend the OS has none.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Saved { get; } = [];

    public bool Unavailable { get; set; }

    public Task SaveAsync(string key, string label, string secret, CancellationToken ct = default)
    {
        ThrowIfUnavailable();
        Saved[key] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key, CancellationToken ct = default)
    {
        ThrowIfUnavailable();
        return Task.FromResult(Saved.GetValueOrDefault(key));
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ThrowIfUnavailable();
        Saved.Remove(key);
        return Task.CompletedTask;
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new SecretStoreUnavailableException("No keyring.");
        }
    }
}
