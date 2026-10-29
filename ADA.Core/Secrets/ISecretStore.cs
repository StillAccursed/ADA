namespace ADA.Core.Secrets;

/// <summary>
/// The OS's own credential store. Secrets are encrypted by the OS for the signed-in user and are
/// never written to ADA's files.
/// </summary>
public interface ISecretStore
{
    /// <summary>Saves <paramref name="secret"/> under <paramref name="key"/>, replacing any secret already there.</summary>
    /// <param name="label">What the OS shows for the entry, e.g. in Keychain Access or Credential Manager.</param>
    /// <exception cref="SecretStoreUnavailableException">This machine has no usable store.</exception>
    Task SaveAsync(string key, string label, string secret, CancellationToken ct = default);

    /// <returns>Null when nothing is saved under <paramref name="key"/>.</returns>
    /// <exception cref="SecretStoreUnavailableException">This machine has no usable store.</exception>
    Task<string?> LoadAsync(string key, CancellationToken ct = default);

    /// <summary>Removes the secret. Does nothing if there isn't one.</summary>
    /// <exception cref="SecretStoreUnavailableException">This machine has no usable store.</exception>
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>The OS has no credential store ADA can use, e.g. a Linux desktop without a keyring.</summary>
public sealed class SecretStoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
