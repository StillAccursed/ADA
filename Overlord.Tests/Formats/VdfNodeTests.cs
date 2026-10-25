using Overlord.Platform.Formats;

namespace Overlord.Tests.Formats;

public class VdfNodeTests
{
    [Fact]
    public void Parses_current_libraryfolders_format()
    {
        var vdf = VdfNode.Parse("""
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "apps" { "526870" "123" }
                }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """);

        var folders = vdf["libraryfolders"]!;
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders["0"]!.GetString("path"));
        Assert.Equal(@"D:\SteamLibrary", folders["1"]!.GetString("path"));
        Assert.Equal("123", folders["0"]!["apps"]!.GetString("526870"));
    }

    [Fact]
    public void Keys_are_case_insensitive_and_comments_are_skipped()
    {
        var vdf = VdfNode.Parse("""
            // written by Steam
            "AppState" { "installdir" "Satisfactory" // trailing
              "StateFlags" "4" }
            """);

        Assert.Equal("Satisfactory", vdf["appstate"]!.GetString("InstallDir"));
        Assert.Equal("4", vdf["AppState"]!.GetString("stateflags"));
    }

    [Theory]
    [InlineData("\"a\" {")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("}")]
    public void Malformed_input_throws_FormatException(string text) =>
        Assert.Throws<FormatException>(() => VdfNode.Parse(text));
}
