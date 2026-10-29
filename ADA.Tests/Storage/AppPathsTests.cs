using ADA.Platform.Storage;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Storage;

public class AppPathsTests
{
    [Fact]
    public void FoldersSitDirectlyUnderTheRoot()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path).EnsureCreated();

        Assert.True(Directory.Exists(Path.Combine(dir.Path, "Databases")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "Config")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "Staging")));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "Logs")));
        Assert.Equal(Path.Combine(dir.Path, "Databases", "catalog.db"), paths.CatalogDatabase);
    }

    [Fact]
    public void EnsureCreatedIsSafeToRepeat()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path).EnsureCreated();
        File.WriteAllText(Path.Combine(paths.Config, "settings.json"), "{}");

        paths.EnsureCreated();

        Assert.True(File.Exists(Path.Combine(paths.Config, "settings.json")));
    }

    [Fact]
    public void DefaultRootMatchesTheOSConvention()
    {
        var root = AppPaths.ForCurrentOS().Root;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(Path.Combine(home, "Library", "Application Support", "ADA"), root);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal(Path.Combine(home, ".ada"), root);
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ADA"), root);
        }
    }
}
