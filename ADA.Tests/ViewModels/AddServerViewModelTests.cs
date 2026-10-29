using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Servers;
using ADA.ViewModels;

namespace ADA.Tests.ViewModels;

public sealed class AddServerViewModelTests
{
    private readonly List<ServerConnection> _tested = [];
    private readonly List<(ServerConnection Server, string Secret)> _added = [];
    private SetupResult _result = Passing();
    private string? _problem;

    private static SetupResult Passing(GameHost? host = GameHost.Linux) =>
        new([new SetupStep("Log in", true, "Logged in."), new SetupStep("Server API", false, "No answer.", Blocking: false)],
            "abc123", "/srv/found/SatisfactoryDedicatedServer", host, new GameBuild(513885, "1.2"));

    private AddServerViewModel Page()
    {
        var page = new AddServerViewModel(
            (draft, _, progress, _) =>
            {
                _tested.Add(draft);
                foreach (var step in _result.Steps)
                {
                    progress.Report(step);
                }
                return Task.FromResult(_result);
            },
            (server, secret) =>
            {
                _added.Add((server, secret));
                return Task.FromResult(SecretPersistence.Saved);
            },
            _ => _problem)
        {
            Name = "Home",
            Address = "192.168.1.20",
            User = "steam",
            Secret = "hunter2",
            GameRoot = "/srv",
        };
        return page;
    }

    [Fact]
    public async Task AddWaitsForAPassingTestAndATrustedIdentity()
    {
        var page = Page();
        Assert.False(page.CanAdd);

        await page.TestCommand.ExecuteAsync(null);
        Assert.Equal("SHA256:abc123", page.Fingerprint);
        Assert.False(page.CanAdd);

        page.TrustsIdentity = true;
        Assert.True(page.CanAdd);
        Assert.True(page.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditingAnythingAfterTheTestMeansTestingAgain()
    {
        var page = Page();
        await page.TestCommand.ExecuteAsync(null);
        page.TrustsIdentity = true;

        page.Address = "192.168.1.21";
        Assert.False(page.CanAdd);
        page.Address = "192.168.1.20";
        Assert.True(page.CanAdd);

        page.Secret = "different";
        Assert.False(page.CanAdd);
    }

    [Fact]
    public async Task ABlockingFailureKeepsAddDisabled()
    {
        _result = new([new SetupStep("Log in", false, "Refused.")], "abc123", null, null, null);
        var page = Page();

        await page.TestCommand.ExecuteAsync(null);
        page.TrustsIdentity = true;

        Assert.False(page.CanAdd);
        Assert.Single(page.Steps);
        Assert.Equal("✕", page.Steps[0].Glyph);
    }

    [Fact]
    public async Task AFolderServerNeedsNoLoginOrIdentityAndSavesNoSecret()
    {
        _result = new([new SetupStep("Open the folder", true, "Opened.")], null, "/Volumes/Share/Server", GameHost.Windows, null);
        var page = Page();
        page.UsesFolder = true;
        Assert.False(page.UsesSftp);

        await page.TestCommand.ExecuteAsync(null);

        Assert.Null(page.Fingerprint);
        Assert.True(page.CanAdd);
        var tested = Assert.Single(_tested);
        Assert.Equal(ServerTransport.Folder, tested.Transport);
        Assert.Equal("", tested.User);

        page.User = "someone"; // a hidden SFTP field isn't part of a folder server
        Assert.True(page.CanAdd);
        await page.AddCommand.ExecuteAsync(null);
        var (server, secret) = Assert.Single(_added);
        Assert.Equal(ServerTransport.Folder, server.Transport);
        Assert.Equal(GameHost.Windows, server.Host);
        Assert.Equal("", server.HostKeyFingerprint);
        Assert.Equal("", secret);
    }

    [Fact]
    public async Task FtpUsesItsOwnPortAPasswordAndATrustedCertificate()
    {
        var page = Page();
        page.UsesKey = true;
        page.KeyPath = "/home/me/.ssh/id_ed25519";

        page.UsesFtp = true;

        Assert.Equal(ServerTransport.Ftp, page.Transport);
        Assert.False(page.UsesSftp);
        Assert.True(page.UsesLogin);
        Assert.False(page.UsesKeyFile);
        Assert.Equal("Password", page.SecretLabel);
        Assert.Equal("FTP port", page.PortLabel);
        Assert.Equal(ServerConnection.DEFAULT_FTP_PORT, page.Port);

        await page.TestCommand.ExecuteAsync(null);
        var tested = Assert.Single(_tested);
        Assert.Equal(ServerTransport.Ftp, tested.Transport);
        Assert.Equal(ServerAuth.Password, tested.Auth);
        Assert.Null(tested.PrivateKeyPath);
        Assert.False(page.CanAdd);

        page.TrustsIdentity = true;
        await page.AddCommand.ExecuteAsync(null);
        var (server, secret) = Assert.Single(_added);
        Assert.Equal("abc123", server.HostKeyFingerprint);
        Assert.Equal("hunter2", secret);

        page.UsesSftp = true;
        Assert.Equal(ServerConnection.DEFAULT_SSH_PORT, page.Port);
    }

    [Fact]
    public async Task AResultWithoutAServerOsCantBeAdded()
    {
        _result = Passing(host: null);
        var page = Page();
        await page.TestCommand.ExecuteAsync(null);
        page.TrustsIdentity = true;

        Assert.False(page.CanAdd);
    }

    [Fact]
    public async Task AddSavesWhatTheTestFoundAndReportsFinished()
    {
        var page = Page();
        ServerConnection? finished = null;
        page.Finished += (_, server) => finished = server;
        await page.TestCommand.ExecuteAsync(null);
        page.TrustsIdentity = true;

        await page.AddCommand.ExecuteAsync(null);

        var (server, secret) = Assert.Single(_added);
        Assert.Equal("/srv/found/SatisfactoryDedicatedServer", server.GameRoot);
        Assert.Equal("abc123", server.HostKeyFingerprint);
        Assert.Equal(GameHost.Linux, server.Host);
        Assert.Equal("hunter2", secret);
        Assert.Equal(_tested[0].Id, server.Id);
        Assert.Same(server, finished);
    }

    [Fact]
    public async Task DetailsTheStoreRefusesArentTested()
    {
        _problem = "Give the server a name.";
        var page = Page();

        await page.TestCommand.ExecuteAsync(null);

        Assert.Equal("Give the server a name.", page.Problem);
        Assert.Empty(_tested);
    }

    [Fact]
    public void BackFinishesWithNothingAdded()
    {
        var page = Page();
        var finished = false;
        page.Finished += (_, server) => finished = server is null;

        page.BackCommand.Execute(null);

        Assert.True(finished);
    }
}
