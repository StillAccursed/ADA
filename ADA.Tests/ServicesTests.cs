using ADA.Platform.Storage;
using ADA.Tests.Fixtures;

namespace ADA.Tests;

public sealed class ServicesTests
{
    [Fact]
    public void ContainerBuildsWithEveryRegistrationSatisfied()
    {
        using var dir = new TempDir();
        // ValidateOnBuild checks every singleton's constructor can be satisfied, without creating them.
        using var provider = Services.Build(new AppPaths(dir.Path).EnsureCreated());
        Assert.NotNull(provider);
    }
}
