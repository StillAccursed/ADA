using ADA.Core.Secrets;
using ADA.Core.Servers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Servers;

public enum SecretPersistence
{
    /// <summary>In the OS secret store; it's there next time ADA starts.</summary>
    Saved,

    /// <summary>This machine has no secret store, so it's kept in memory and asked for again next launch.</summary>
    ThisSessionOnly,
}

/// <summary>
/// Each server's password or key passphrase. Kept in the OS secret store; when there isn't one,
/// kept in memory until ADA closes and never written to disk.
/// </summary>
public sealed class ServerSecrets(ISecretStore store, ILogger<ServerSecrets>? log = null)
{
    private readonly ILogger _log = log ?? NullLogger<ServerSecrets>.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, string> _session = [];

    /// <summary>Saves the secret, in memory for this session if the OS can't store it.</summary>
    /// <returns>Where it ended up, so the UI can say it will be asked for again.</returns>
    public async Task<SecretPersistence> SaveAsync(ServerConnection server, string secret, CancellationToken ct = default)
    {
        try
        {
            await store.SaveAsync(Key(server.Id), $"ADA: {server.Name}", secret, ct);
            Forget(server.Id);
            return SecretPersistence.Saved;
        }
        catch (SecretStoreUnavailableException e)
        {
            _log.LogWarning("No OS secret store ({Reason}); keeping {Server}'s secret for this session only", e.Message, server.Name);
            lock (_gate)
            {
                _session[server.Id] = secret;
            }
            return SecretPersistence.ThisSessionOnly;
        }
    }

    /// <returns>Null when nothing is saved, so the user must be asked.</returns>
    public async Task<string?> LoadAsync(Guid serverId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_session.TryGetValue(serverId, out var remembered))
            {
                return remembered;
            }
        }
        try
        {
            return await store.LoadAsync(Key(serverId), ct);
        }
        catch (SecretStoreUnavailableException e)
        {
            _log.LogWarning("Couldn't read a server secret: {Reason}", e.Message);
            return null;
        }
    }

    /// <summary>Removes the secret from the OS store and from memory. A store that can't be reached is logged, not thrown.</summary>
    public async Task DeleteAsync(Guid serverId, CancellationToken ct = default)
    {
        Forget(serverId);
        try
        {
            await store.DeleteAsync(Key(serverId), ct);
        }
        catch (SecretStoreUnavailableException e)
        {
            _log.LogWarning("Couldn't remove a server secret: {Reason}", e.Message);
        }
    }

    private static string Key(Guid serverId) => $"server:{serverId:N}";

    private void Forget(Guid serverId)
    {
        lock (_gate)
        {
            _session.Remove(serverId);
        }
    }
}
