using System.Text;
using ADA.Core.Profiles;
using ADA.Core.Saves;
using ADA.Infrastructure.Installing;
using ADA.Platform.Formats;
using ADA.Tests.Fixtures;
using ADA.ViewModels;

namespace ADA.Tests.ViewModels;

public class SaveDetailsViewModelTests
{
    private const string THREE_MODS = """
        {"Version":1,"Mods":[
          {"Reference":"LongerBeams","Name":"Longer Beams","Version":"1.0.2"},
          {"Reference":"Achievements","Name":"Achievements","Version":"2.0.7"},
          {"Reference":"ExtraSwatchSlots","Name":"64 Extra Swatch Slots","Version":"1.0.2"}
        ]}
        """;

    private static SaveGame Save(SaveHeaderBuilder header, string path = "/saves/acct/Atria_autosave_2.sav") =>
        new(path, "acct", 2048, DateTime.UtcNow, SaveHeaderReader.Read(header.ToStream())!);

    private static InstalledMod Installed(string reference, string version) =>
        new(reference, version, false, $"Mods/{reference}", DateTimeOffset.UtcNow, []);

    private static SaveDetailsViewModel Details(SaveHeaderBuilder header, IReadOnlyDictionary<string, InstalledMod>? installed = null) =>
        new(Save(header), installed);

    private static string Variant(int type, byte[] value) => SaveHeaderBuilder.Variant(type, value);

    [Fact]
    public void ComparesModsWithWhatIsInstalled()
    {
        var installed = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase)
        {
            ["longerbeams"] = Installed("LongerBeams", "1.0.2"),
            ["Achievements"] = Installed("Achievements", "2.1.0"),
        };

        var details = Details(new SaveHeaderBuilder().ModMetadata(THREE_MODS), installed);

        Assert.Equal(
            [(SaveModStatus.Missing, "Not installed"), (SaveModStatus.DifferentVersion, "2.1.0 installed"), (SaveModStatus.Installed, "Installed")],
            details.ShownMods.Select(m => (m.Status, m.StatusText)));
        Assert.Equal("3 mods · 1 not installed · 1 different version", details.ModsSummary);
    }

    [Fact]
    public void ModsHaveNoStatusWhenInstalledModsCouldNotBeRead()
    {
        var details = Details(new SaveHeaderBuilder().ModMetadata(THREE_MODS), installed: null);

        Assert.All(details.ShownMods, m => Assert.Equal("", m.StatusText));
        Assert.Equal("3 mods", details.ModsSummary);
    }

    [Fact]
    public void ModSearchMatchesNameOrReference()
    {
        var details = Details(new SaveHeaderBuilder().ModMetadata(THREE_MODS));

        details.ModSearch = "swatch";
        Assert.Equal(["64 Extra Swatch Slots"], details.ShownMods.Select(m => m.Name));

        details.ModSearch = "  ";
        Assert.Equal(3, details.ShownMods.Count);
    }

    [Theory]
    [InlineData(false, "No mods")]
    [InlineData(true, "Marked as modded, but no mods are recorded")]
    public void SummarisesSavesWithoutRecordedMods(bool modded, string expected)
    {
        var header = modded ? new SaveHeaderBuilder().ModMetadata("""{"Mods":[]}""") : new SaveHeaderBuilder();

        Assert.Equal(expected, Details(header).ModsSummary);
    }

    [Fact]
    public void GroupsSessionSettingsUnderModNames()
    {
        var one = Variant(14, BitConverter.GetBytes(1));
        var options = "?SessionSettings="
            + $"LongerBeams.BeamLength={one},CycleBuffers.Input={one},PC620_WU_Ammo={one},longerbeams.Scroll={one}";

        var details = Details(new SaveHeaderBuilder().ModMetadata(THREE_MODS).MapOptions(options));

        Assert.Equal(["CycleBuffers (1)", "Longer Beams (2)", "Other settings (1)"], details.SettingGroups.Select(g => g.Header));
    }

    [Fact]
    public void FormatsSettingValues()
    {
        Assert.Equal("On", SessionSettingRowViewModel.Format(new SessionSettingValue.Flag(true)));
        Assert.Equal("Off", SessionSettingRowViewModel.Format(new SessionSettingValue.Flag(false)));
        Assert.Equal(10.ToString(), SessionSettingRowViewModel.Format(new SessionSettingValue.Integer(10)));
        Assert.Equal(0.1f.ToString(), SessionSettingRowViewModel.Format(new SessionSettingValue.Number(0.1f)));
        Assert.Equal("(not readable)", SessionSettingRowViewModel.Format(new SessionSettingValue.Unknown(26, 3)));
    }

    [Theory]
    [InlineData(0f, 0f, 0f, "#000000")]
    [InlineData(1f, 1f, 1f, "#FFFFFF")]
    [InlineData(0.5f, 0.5f, 0.5f, "#BCBCBC")]
    [InlineData(2f, -1f, 0f, "#FF0000")]
    public void ColoursAreShownInSrgb(float r, float g, float b, string hex)
    {
        Assert.Equal(hex, SessionSettingRowViewModel.Format(new SessionSettingValue.Colour(r, g, b, 1f)));
    }

    [Fact]
    public void ColourSettingsHaveASwatch()
    {
        var colour = new[] { 1f, 0f, 0f, 1f }.SelectMany(BitConverter.GetBytes).ToArray();
        var details = Details(new SaveHeaderBuilder().MapOptions($"?SessionSettings=BasherMk2.Color={Variant(17, colour)}"));

        var row = Assert.Single(Assert.Single(details.SettingGroups).Settings);
        Assert.True(row.HasSwatch);
    }

    [Fact]
    public void ShowsGameFactsFromTheHeader()
    {
        var details = Details(new SaveHeaderBuilder().AdvancedGameSettings().Visibility(1));

        Assert.Equal("On", details.AdvancedGameSettingsText);
        Assert.Equal("Skipped", details.TutorialText);
        Assert.Equal("Friends only", details.VisibilityText);
        Assert.Equal("On", details.PartitionedWorldText);
        Assert.Equal("0bV9f0YUAB7qx-SKOn2I2Q", details.SaveIdText);
        Assert.Equal("CL 513885", details.BuildText);
        Assert.Equal("Header 14 · Save 60", details.FormatText);
    }

    [Fact]
    public void FactsTheHeaderVersionDoesNotRecordAreUnknown()
    {
        var details = Details(new SaveHeaderBuilder().HeaderVersion(13).Visibility(7));

        Assert.Equal("Unknown", details.AdvancedGameSettingsText);
        Assert.Equal("Unknown", details.PartitionedWorldText);
        Assert.Equal("Unknown", details.SaveIdText);
        Assert.Equal("Unknown", details.VisibilityText);
    }

    [Fact]
    public void OtherOptionsLeaveOutTheTutorialFlag()
    {
        var details = Details(new SaveHeaderBuilder().MapOptions("?skiponboarding?startloc=Grass Fields"));

        Assert.Equal([new SaveOption("startloc", "Grass Fields")], details.OtherOptions);
    }

    [Fact]
    public void MissingReferencesAreOnlyTheModsNotInstalled()
    {
        var installed = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase)
        {
            ["LongerBeams"] = Installed("LongerBeams", "1.0.2"),
            ["Achievements"] = Installed("Achievements", "2.1.0"),
        };

        var details = Details(new SaveHeaderBuilder().ModMetadata(THREE_MODS), installed);

        Assert.Equal(["ExtraSwatchSlots"], details.MissingReferences);
        Assert.Equal("Install 1 missing mod", details.InstallMissingText);
    }

    [Fact]
    public void NothingIsMissingWhenInstalledModsCouldNotBeRead()
    {
        Assert.False(Details(new SaveHeaderBuilder().ModMetadata(THREE_MODS), installed: null).HasMissingMods);
    }

    [Fact]
    public void ProfileHoldsTheSavesModsAtTheirVersions()
    {
        var details = Details(new SaveHeaderBuilder().ModMetadata("""
            {"Mods":[{"Reference":"A","Version":"1.0.0"},{"Reference":"a","Version":"1.0.0"},{"Reference":"NoVersion"}]}
            """));

        var profile = details.ToProfile("Atria");

        Assert.Equal("Atria", profile.Name);
        Assert.Equal([new ProfileMod("A", "1.0.0")], profile.Mods);
    }

    [Theory]
    [InlineData(0, "0 m")]
    [InlineData(625, "10 m")]
    [InlineData(3600, "1 h 0 m")]
    [InlineData(511_800, "142 h 10 m")]
    public void PlayTimeKeepsCountingHours(int seconds, string expected)
    {
        Assert.Equal(expected, SaveText.PlayTime(TimeSpan.FromSeconds(seconds)));
    }
}
