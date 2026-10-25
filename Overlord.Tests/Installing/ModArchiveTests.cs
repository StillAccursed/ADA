using System.IO.Compression;
using Overlord.Core.Catalog;
using Overlord.Infrastructure.Installing;
using Overlord.Tests.Fixtures;

namespace Overlord.Tests.Installing;

public sealed class ModArchiveTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Reads_a_flat_archive()
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
    public void Reads_the_target_folder_of_a_multi_target_archive()
    {
        var path = new SmodBuilder("Mod", "1.0.0").InFolder("Windows").Typical().WriteTo(_dir.Path);

        using var archive = ModArchive.Open(path, TargetPlatform.Windows);

        Assert.Equal("Mod", archive.Descriptor.Reference);
        Assert.All(archive.Entries, e => Assert.False(e.RelativePath.StartsWith("Windows/")));
        Assert.False(archive.Descriptor.GameFeature);
    }

    [Fact]
    public void Multi_target_archive_without_the_requested_platform_is_rejected()
    {
        var path = new SmodBuilder("Mod", "1.0.0").InFolder("Windows").Typical().WriteTo(_dir.Path);

        var e = Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.LinuxServer));
        Assert.Contains("LinuxServer", e.Message);
    }

    [Fact]
    public void Can_leave_out_debug_symbols()
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
    public void Rejects_paths_that_escape_the_mod_folder(string entryName)
    {
        var path = new SmodBuilder("Mod", "1.0.0").File(entryName, "x").WriteTo(_dir.Path);

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Fact]
    public void Rejects_two_uplugins_at_the_root()
    {
        var path = new SmodBuilder("Mod", "1.0.0").File("Other.uplugin", """{ "SemVersion": "1.0.0" }""").WriteTo(_dir.Path);

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Fact]
    public void Rejects_names_that_differ_only_by_case()
    {
        var path = new SmodBuilder("Mod", "1.0.0").File("Content/a.pak", "1").File("content/A.pak", "2").WriteTo(_dir.Path);

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "FriendlyName": "no version" }""")]
    public void Rejects_a_bad_uplugin(string uplugin)
    {
        var path = new SmodBuilder("Mod", "1.0.0").RawUPlugin(uplugin).WriteTo(_dir.Path);

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Fact]
    public void Accepts_comments_and_trailing_commas_in_the_uplugin()
    {
        var path = new SmodBuilder("Mod", "1.0.0")
            .RawUPlugin("""{ /* hand edited */ "SemVersion": "2.0.0", "GameFeature": true, }""")
            .WriteTo(_dir.Path);

        using var archive = ModArchive.Open(path, TargetPlatform.Windows);
        Assert.Equal("2.0.0", archive.Descriptor.Version);
        Assert.True(archive.Descriptor.GameFeature);
    }

    [Fact]
    public void Rejects_a_file_that_is_not_a_zip()
    {
        var path = _dir.WriteFile("broken.smod", "this is not a zip");

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }

    [Fact]
    public void Rejects_an_archive_with_no_uplugin()
    {
        var path = _dir.Combine("empty.smod");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");

        Assert.Throws<InvalidModArchiveException>(() => ModArchive.Open(path, TargetPlatform.Windows));
    }
}
