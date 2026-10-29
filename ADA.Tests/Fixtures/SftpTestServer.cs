using ADA.Core.Games;
using ADA.Core.Servers;
using Renci.SshNet;

namespace ADA.Tests.Fixtures;

/// <summary>
/// For tests that need a real SFTP server. Set <c>ADA_TEST_SFTP</c> to
/// <c>user@host:port/absolute/folder</c> and <c>ADA_TEST_SFTP_PASSWORD</c> to the password; macOS's
/// Remote Login (System Settings → General → Sharing) is enough. Reported as skipped otherwise.
/// </summary>
public sealed class SftpFactAttribute : FactAttribute
{
    public SftpFactAttribute()
    {
        if (SftpTestServer.Settings is null)
        {
            Skip = "Needs an SFTP server; set ADA_TEST_SFTP=user@host:port/folder and ADA_TEST_SFTP_PASSWORD to run it.";
        }
    }
}

/// <summary>A connected client and a fresh folder on the test server, deleted afterwards.</summary>
public sealed class SftpTestServer : IDisposable
{
    public SftpTestServer()
    {
        var settings = Settings ?? throw new InvalidOperationException("ADA_TEST_SFTP isn't set.");
        Server = settings with { GameRoot = $"{settings.GameRoot.TrimEnd('/')}/ada-test-{Guid.NewGuid():N}" };
        Client = new SftpClient(Server.Address, Server.Port, Server.User, Password);
        Client.Connect();
        Client.CreateDirectory(Server.GameRoot);
        Client.CreateDirectory($"{Server.GameRoot}/FactoryGame");
    }

    /// <summary>The test server as a saved server whose game root is this test's own folder.</summary>
    public ServerConnection Server { get; }

    public SftpClient Client { get; }

    public static string Password => Environment.GetEnvironmentVariable("ADA_TEST_SFTP_PASSWORD") ?? "";

    /// <summary>The server from <c>ADA_TEST_SFTP</c>, or null when it isn't set or can't be read.</summary>
    public static ServerConnection? Settings
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("ADA_TEST_SFTP");
            var at = value?.IndexOf('@') ?? -1;
            var slash = value?.IndexOf('/', Math.Max(at, 0)) ?? -1;
            if (value is null || at <= 0 || slash < 0)
            {
                return null;
            }
            var hostPort = value[(at + 1)..slash].Split(':');
            var port = hostPort.Length > 1 && int.TryParse(hostPort[1], out var p) ? p : ServerConnection.DEFAULT_SSH_PORT;
            return new ServerConnection(Guid.NewGuid(), "Test server", hostPort[0], port, value[..at], ServerAuth.Password,
                value[slash..], GameHost.Linux, "");
        }
    }

    public void Dispose()
    {
        DeleteTree(Server.GameRoot);
        Client.Dispose();
    }

    private void DeleteTree(string folder)
    {
        foreach (var entry in Client.ListDirectory(folder).Where(e => e.Name is not ("." or "..")).ToList())
        {
            if (entry.IsDirectory)
            {
                DeleteTree(entry.FullName);
            }
            else
            {
                Client.DeleteFile(entry.FullName);
            }
        }
        Client.DeleteDirectory(folder);
    }
}
