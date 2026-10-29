using System.Runtime.Versioning;
using CliWrap;
using CliWrap.Buffered;
using ADA.Core.Secrets;

namespace ADA.Platform.Secrets;

/// <summary>
/// The desktop's Secret Service (GNOME Keyring, KWallet) through <c>secret-tool</c>. Entries carry
/// the attributes <c>application=ADA</c> and <c>key={key}</c>. The secret goes in on standard
/// input and comes back on standard output; it is never a command-line argument.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSecretServiceStore : ISecretStore
{
    private const string TOOL = "secret-tool";

    // secret-tool answers at once, unless the keyring asks to be unlocked; give the user time for that.
    private static readonly TimeSpan COMMAND_TIMEOUT = TimeSpan.FromMinutes(2);

    public async Task SaveAsync(string key, string label, string secret, CancellationToken ct = default)
    {
        var result = await RunAsync(["store", "--label", label, .. Attributes(key)], secret, ct);
        if (result.ExitCode != 0)
        {
            throw Unavailable(result);
        }
    }

    public async Task<string?> LoadAsync(string key, CancellationToken ct = default)
    {
        var result = await RunAsync(["lookup", .. Attributes(key)], null, ct);
        if (result.ExitCode == 0)
        {
            return result.StandardOutput;
        }
        // Not found is exit code 1 with nothing on stderr; a missing or locked service says why.
        return string.IsNullOrWhiteSpace(result.StandardError) ? null : throw Unavailable(result);
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var result = await RunAsync(["clear", .. Attributes(key)], null, ct);
        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw Unavailable(result);
        }
    }

    private static string[] Attributes(string key) => ["application", SecretStores.SERVICE_NAME, "key", key];

    /// <exception cref="SecretStoreUnavailableException"><c>secret-tool</c> isn't installed or didn't answer.</exception>
    private static async Task<BufferedCommandResult> RunAsync(string[] args, string? input, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(COMMAND_TIMEOUT);
        try
        {
            var command = Cli.Wrap(TOOL).WithArguments(args).WithValidation(CommandResultValidation.None);
            if (input is not null)
            {
                command = command.WithStandardInputPipe(PipeSource.FromString(input));
            }
            return await command.ExecuteBufferedAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SecretStoreUnavailableException("The keyring didn't answer in time.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SecretStoreUnavailableException("No keyring was found: secret-tool isn't installed.", e);
        }
    }

    private static SecretStoreUnavailableException Unavailable(BufferedCommandResult result) =>
        new($"The keyring couldn't be used: {result.StandardError.Trim()}");
}
