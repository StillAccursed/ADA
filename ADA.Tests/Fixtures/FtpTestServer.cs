using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Servers;

namespace ADA.Tests.Fixtures;

/// <summary>
/// For tests that need a real FTPS server. Set <c>ADA_TEST_FTP</c> to <c>user@host:port/absolute/folder</c>
/// and <c>ADA_TEST_FTP_PASSWORD</c> to the password; the server must offer explicit TLS (for example
/// pure-ftpd with TLS in Docker). Reported as skipped otherwise.
/// </summary>
public sealed class FtpFactAttribute : FactAttribute
{
    public FtpFactAttribute()
    {
        if (FtpTestServer.Settings is null)
        {
            Skip = "Needs an FTPS server; set ADA_TEST_FTP=user@host:port/folder and ADA_TEST_FTP_PASSWORD to run it.";
        }
    }
}

/// <summary>A connected session and a fresh folder on the test server, deleted afterwards.</summary>
internal sealed class FtpTestServer : IDisposable
{
    private FtpTestServer(ServerConnection server, FluentFtpSession session)
    {
        Server = server;
        Session = session;
    }

    /// <summary>The test server as a saved server whose game root is this test's own folder.</summary>
    public ServerConnection Server { get; }

    public FluentFtpSession Session { get; }

    public static string Password => Environment.GetEnvironmentVariable("ADA_TEST_FTP_PASSWORD") ?? "";

    /// <summary>The server from <c>ADA_TEST_FTP</c>, or null when it isn't set or can't be read.</summary>
    public static ServerConnection? Settings
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("ADA_TEST_FTP");
            var at = value?.IndexOf('@') ?? -1;
            var slash = value?.IndexOf('/', Math.Max(at, 0)) ?? -1;
            if (value is null || at <= 0 || slash < 0)
            {
                return null;
            }
            var hostPort = value[(at + 1)..slash].Split(':');
            var port = hostPort.Length > 1 && int.TryParse(hostPort[1], out var p) ? p : ServerConnection.DEFAULT_FTP_PORT;
            return new ServerConnection(Guid.NewGuid(), "Test FTP server", hostPort[0], port, value[..at], ServerAuth.Password,
                value[slash..], GameHost.Linux, "", Transport: ServerTransport.Ftp);
        }
    }

    /// <summary>Connects through <paramref name="connections"/>, trusting the certificate, and makes a fresh folder with <c>FactoryGame</c>.</summary>
    public static async Task<FtpTestServer> StartAsync(ServerConnections connections)
    {
        var settings = Settings ?? throw new InvalidOperationException("ADA_TEST_FTP isn't set.");
        var server = settings with { GameRoot = $"{settings.GameRoot.TrimEnd('/')}/ada-test-{Guid.NewGuid():N}" };
        var session = await connections.OpenFtpAsync(server, CancellationToken.None, Password);
        session.CreateDirectories($"{server.GameRoot}/FactoryGame");
        return new FtpTestServer(server, session);
    }

    public void Dispose()
    {
        try
        {
            Session.DeleteDirectory(Server.GameRoot);
        }
        finally
        {
            Session.Dispose();
        }
    }
}
