using System.IO.Compression;
using ADA.Core.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Installing;

public sealed class ModArchiveTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ReadsAFlatArchive()
    {
        var path = new SmodBuilder("WeatherExtended", "1.1.0").GameFeature().Typical().WriteTo(_dir.Path);

        using var archive = ModArchive.Open(path, TargetPlatform.Windows);

        Assert.Equal("WeatherExtended", archive.Descriptor.Reference);
        Assert.Equal("1.1.0", archive.Descriptor.Version);
        Assert.True(archive.Descriptor.GameFeature);
        Assert.Equal(">=513885", archive.Descriptor.GameVersion);
        Assert.Equal(new UPluginDependency("SML", "^3.12.0", false), Assert.Single(archive.Descriptor.Dependencies));
        Assert.Contains(archive.Entries, e => e.RelativePath == "WeatherExtended.uplugin");
        Assert.Equal(5, archive.Entries.Count);
    }

    [Fact]
    public void ReadsTheTargetFolderOfAMultiTargetArchive()
    {
        var path = new SmodBuilder("Mod", "1.0.0").InFolder("Windows").Typical().WriteTo(_dir.Path);

        using var archive = ModArchive.Open(path, TargetPlatform.Windows);

        Assert.Equal("Mod", archive.Descriptor.Reference);
        Assert.All(archive.Entries, e => Assert.False(e.RelativePath.StartsWith("Windows/")));
        Assert.False(archive.Descriptor.GameFeature);
    }

    [Fact]
    public void MultiTargetArchiveWithoutTheRequestedPlatformIsRejected()
    {
        var path = new SmodBuilder("Mod", "1.0.0").InFolder("Windows").Typical().WriteTo(_dir.Path);

        var e = Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.LinuxServer));
        Assert.Contains("LinuxServer", e.Message);
    }

    [Fact]
    public void CanLeaveOutDebugSymbols()
    {
        var path = new SmodBuilder("Mod", "1.0.0").Typical().WriteTo(_dir.Path);

        using var archive = ModArchive.Open(path, TargetPlatform.Windows, includeDebugSymbols: false);

        Assert.DoesNotContain(archive.Entries, e => e.RelativePath.EndsWith(".pdb"));
        Assert.Equal(4, archive.Entries.Count);
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("Binaries/../../evil.dll")]
    [InlineData("/etc/evil")]
    [InlineData("C:/evil.dll")]
    [InlineData("Binaries\\..\\..\\evil.dll")]
    public void RejectsPathsThatEscapeTheModFolder(string entryName)
    {
        var path = new SmodBuilder("Mod", "1.0.0").File(entryName, "x").WriteTo(_dir.Path);

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Fact]
    public void RejectsTwoUpluginsAtTheRoot()
    {
        var path = new SmodBuilder("Mod", "1.0.0").File("Other.uplugin", """{ "SemVersion": "1.0.0" }""").WriteTo(_dir.Path);

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Fact]
    public void RejectsNamesThatDifferOnlyByCase()
    {
        var path = new SmodBuilder("Mod", "1.0.0").File("Content/a.pak", "1").File("content/A.pak", "2").WriteTo(_dir.Path);

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "FriendlyName": "no version" }""")]
    public void RejectsABadUplugin(string uplugin)
    {
        var path = new SmodBuilder("Mod", "1.0.0").RawUPlugin(uplugin).WriteTo(_dir.Path);

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Fact]
    public void AcceptsCommentsAndTrailingCommasInTheUplugin()
    {
        var path = new SmodBuilder("Mod", "1.0.0")
            .RawUPlugin("""{ /* hand edited */ "SemVersion": "2.0.0", "GameFeature": true, }""")
            .WriteTo(_dir.Path);

        using var archive = ModArchive.Open(path, TargetPlatform.Windows);
        Assert.Equal("2.0.0", archive.Descriptor.Version);
        Assert.True(archive.Descriptor.GameFeature);
    }

    [Fact]
    public void RejectsAFileThatIsNotAZip()
    {
        var path = _dir.WriteFile("broken.smod", "this is not a zip");

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Fact]
    public void RejectsAnArchiveWithNoUplugin()
    {
        var path = _dir.Combine("empty.smod");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readme.txt");
        }

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Fact]
    public void RefusesAnEntryCompressedLikeAZipBomb()
    {
        // 32 MB of one repeated byte deflates about 1000:1; no real mod file comes close.
        var path = new SmodBuilder("Bomb", "1.0.0").File("Content/Paks/Bomb.pak", new string('0', 32 * 1024 * 1024))
            .WriteTo(_dir.Combine("bomb"));

        var e = Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
        Assert.Contains("compressed more than", e.Message);
    }

    [Fact]
    public void TheSizeCeilingAllowsLargeMods()
    {
        // Whole profiles reach tens of GB; one mod must not be capped anywhere near a few GB.
        Assert.True(ModArchive.MAX_UNCOMPRESSED_BYTES >= 32L * 1024 * 1024 * 1024);
    }
}
