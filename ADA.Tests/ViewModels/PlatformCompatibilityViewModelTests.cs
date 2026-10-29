using ADA.Core.Catalog;
using ADA.Tests.Fixtures;
using ADA.ViewModels;

namespace ADA.Tests.ViewModels;

public sealed class PlatformCompatibilityViewModelTests
{
    private static LatestVersion[] Release(params TargetPlatform[] targets) =>
        [new LatestVersion(VersionStability.Release, "1.2.0", ">=513885", targets)];

    [Theory]
    [InlineData(new[] { TargetPlatform.Windows, TargetPlatform.WindowsServer, TargetPlatform.LinuxServer }, "Windows and Linux servers in 1.2.0")]
    [InlineData(new[] { TargetPlatform.Windows, TargetPlatform.WindowsServer }, "Windows servers only in 1.2.0")]
    [InlineData(new[] { TargetPlatform.LinuxServer }, "Linux servers only in 1.2.0")]
    public void ServerIndicatorNamesTheServerSystemsWithBuilds(TargetPlatform[] targets, string description)
    {
        var server = PlatformCompatibilityViewModel.ForServer(Release(targets));

        Assert.True(server.IsCompatible);
        Assert.False(server.IsNotNeeded);
        Assert.Equal("✓", server.Glyph);
        Assert.Equal(description, server.Description);
    }

    [Fact]
    public void ClientIndicatorFollowsTheWindowsBuild()
    {
        var client = PlatformCompatibilityViewModel.ForClient(Release(TargetPlatform.Windows));

        Assert.True(client.IsCompatible);
        Assert.Equal("Has a client build in 1.2.0", client.Description);
    }

    [Fact]
    public void MissingServerBuildIsNotCompatibleUntilTheFullVersionSaysOtherwise()
    {
        var server = PlatformCompatibilityViewModel.ForServer(Release(TargetPlatform.Windows));

        Assert.False(server.IsCompatible);
        Assert.False(server.IsNotNeeded);
        Assert.Equal("✕", server.Glyph);
        Assert.Equal("No server build in 1.2.0", server.Description);
    }

    [Fact]
    public void ClientOnlyModShowsAsNotNeededOnServers()
    {
        var server = PlatformCompatibilityViewModel.ForServer(Release(TargetPlatform.Windows), requiredOnRemote: false);

        Assert.False(server.IsCompatible);
        Assert.True(server.IsNotNeeded);
        Assert.Equal("–", server.Glyph);
        Assert.Equal("Client-only in 1.2.0: servers don't need it", server.Description);
    }

    [Fact]
    public void ModPlayersNeedButServersCantRunIsNotCompatible()
    {
        var server = PlatformCompatibilityViewModel.ForServer(Release(TargetPlatform.Windows), requiredOnRemote: true);

        Assert.False(server.IsNotNeeded);
        Assert.Equal("✕", server.Glyph);
    }

    [Fact]
    public void ServerOnlyModShowsAsNotNeededOnClients()
    {
        var client = PlatformCompatibilityViewModel.ForClient(Release(TargetPlatform.LinuxServer), requiredOnRemote: false);

        Assert.True(client.IsNotNeeded);
        Assert.Equal("Server-only in 1.2.0: clients don't need it", client.Description);
    }

    [Fact]
    public void ReleaseIsPreferredOverNewerBetaAndBetaIsUsedWithoutRelease()
    {
        LatestVersion[] both =
        [
            new(VersionStability.Beta, "2.0.0-beta", ">=513885", [TargetPlatform.Windows, TargetPlatform.WindowsServer]),
            new(VersionStability.Release, "1.2.0", ">=513885", [TargetPlatform.Windows]),
        ];
        LatestVersion[] betaOnly = [both[0]];

        Assert.Equal("No server build in 1.2.0", PlatformCompatibilityViewModel.ForServer(both).Description);
        Assert.Equal("Windows servers only in 2.0.0-beta", PlatformCompatibilityViewModel.ForServer(betaOnly).Description);
    }

    [Fact]
    public void UnknownBuildsAreNeitherCompatibleNorNotNeeded()
    {
        var server = PlatformCompatibilityViewModel.ForServer(null, requiredOnRemote: false);

        Assert.False(server.IsCompatible);
        Assert.False(server.IsNotNeeded);
        Assert.Equal("✕", server.Glyph);
        Assert.Equal("Builds not known yet", server.Description);
    }

    [Fact]
    public void ModItemStartsFromTheCachedLatestVersions()
    {
        var mod = new ModItemViewModel(CatalogFixtures.Mod("Example") with { Latest = Release(TargetPlatform.Windows) });

        Assert.True(mod.ClientCompatibility.IsCompatible);
        Assert.Equal("No server build in 1.2.0", mod.ServerCompatibility.Description);
    }
}
