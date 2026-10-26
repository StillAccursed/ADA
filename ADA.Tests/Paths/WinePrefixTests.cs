using ADA.Platform.Paths;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Paths;

public class WinePrefixTests
{
    [UnixFact]
    public void Resolves_mapped_drive_through_dosdevices_symlink()
    {
        using var dir = new TempDir();
        var home = Directory.CreateDirectory(dir.Combine("home", "Games", "Satisfactory")).Parent!.Parent!.FullName;
        var bottle = Directory.CreateDirectory(dir.Combine("bottle", "dosdevices")).Parent!.FullName;
        Directory.CreateSymbolicLink(Path.Combine(bottle, "dosdevices", "y:"), home);

        var host = new WinePrefix(bottle).ToHostPath(@"Y:\Games\Satisfactory");

        Assert.Equal(Path.Combine(home, "Games", "Satisfactory"), host);
    }

    [Fact]
    public void Falls_back_to_drive_c_and_matches_case_insensitively()
    {
        using var dir = new TempDir();
        var bottle = dir.Combine("bottle");
        var programData = Directory.CreateDirectory(Path.Combine(bottle, "drive_c", "ProgramData", "Epic")).FullName;

        Assert.Equal(programData, new WinePrefix(bottle).ToHostPath(@"c:\PROGRAMDATA\epic\"));
    }

    [Theory]
    [InlineData(@"Q:\Nowhere")]
    [InlineData(@"C:\Missing\Folder")]
    [InlineData(@"\\server\share")]
    [InlineData("relative")]
    public void Returns_null_for_unmapped_or_missing_paths(string windowsPath)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("bottle", "drive_c"));

        Assert.Null(new WinePrefix(dir.Combine("bottle")).ToHostPath(windowsPath));
    }
}
