using ADA.Core.Secrets;

namespace ADA.Platform.Secrets;

/// <summary>Picks the <see cref="ISecretStore"/> for the OS ADA is running on.</summary>
public static class SecretStores
{
    /// <summary>Every entry ADA saves is filed under this name, so it's easy to find and remove.</summary>
    internal const string SERVICE_NAME = "ADA";

    public static ISecretStore ForCurrentOS()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxSecretServiceStore();
        }

        throw new PlatformNotSupportedException("ADA supports Windows, Linux and macOS.");
    }
}
