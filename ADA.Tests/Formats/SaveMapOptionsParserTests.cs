using ADA.Core.Saves;
using ADA.Platform.Formats;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Formats;

public class SaveMapOptionsParserTests
{
    private const int VARIANT_BOOL = 2;
    private const int VARIANT_FLOAT = 10;
    private const int VARIANT_INT32 = 14;
    private const int VARIANT_LINEAR_COLOR = 17;

    [Fact]
    public void KeepsPlainOptionsInOrder()
    {
        var options = SaveMapOptionsParser.Parse("?skiponboarding?startloc=Grass Fields");

        Assert.Equal([new SaveOption("skiponboarding", ""), new SaveOption("startloc", "Grass Fields")], options.Options);
        Assert.True(options.SkippedOnboarding);
    }

    [Fact]
    public void LeavesOutAccountIdentity()
    {
        var options = SaveMapOptionsParser.Parse(
            "?ClientIdentity=00ff?PlayerInfoSnapshot=0500?fallbackaccountnames=0000?skiponboarding");

        Assert.Equal("skiponboarding", Assert.Single(options.Options).Key);
    }

    [Fact]
    public void DecodesEachSessionSettingType()
    {
        var colour = new[] { 0.5f, 0f, 1f, 1f }.SelectMany(BitConverter.GetBytes).ToArray();
        var options = SaveMapOptionsParser.Parse("?SessionSettings="
            + $"Lithium.AddParts={SaveHeaderBuilder.Variant(VARIANT_BOOL, BitConverter.GetBytes(1))},"
            + $"CycleBuffers.InputBufferCycles={SaveHeaderBuilder.Variant(VARIANT_INT32, BitConverter.GetBytes(3))},"
            + $"LongerBeams.BeamLength={SaveHeaderBuilder.Variant(VARIANT_FLOAT, BitConverter.GetBytes(100f))},"
            + $"BasherMk2.Color={SaveHeaderBuilder.Variant(VARIANT_LINEAR_COLOR, colour)}");

        Assert.Equal(
        [
            new SessionSettingValue.Flag(true),
            new SessionSettingValue.Integer(3),
            new SessionSettingValue.Number(100f),
            new SessionSettingValue.Colour(0.5f, 0f, 1f, 1f),
        ], options.SessionSettings.Select(s => s.Value));
        Assert.Empty(options.Options);
    }

    [Fact]
    public void SplitsModPrefixFromSettingName()
    {
        var value = SaveHeaderBuilder.Variant(VARIANT_FLOAT, BitConverter.GetBytes(1f));
        var options = SaveMapOptionsParser.Parse($"?SessionSettings=LongerBeams.BeamLength={value},PC620_WU_Ammo_Damage={value}");

        Assert.Collection(options.SessionSettings,
            s =>
            {
                Assert.Equal("LongerBeams", s.ModReference);
                Assert.Equal("BeamLength", s.Name);
                Assert.Equal("LongerBeams.BeamLength", s.Key);
            },
            s =>
            {
                Assert.Null(s.ModReference);
                Assert.Equal("PC620_WU_Ammo_Damage", s.Name);
            });
    }

    [Fact]
    public void UnknownOrMalformedValuesDoNotHideOtherSettings()
    {
        var options = SaveMapOptionsParser.Parse("?SessionSettings="
            + $"A.Name={SaveHeaderBuilder.Variant(26, [1, 2, 3])},"
            + "B.Broken=not*base64,"
            + $"C.Short={Convert.ToBase64String([1, 2])},"
            + $"D.Ok={SaveHeaderBuilder.Variant(VARIANT_INT32, BitConverter.GetBytes(7))}");

        Assert.Equal(
        [
            new SessionSettingValue.Unknown(26, 3),
            new SessionSettingValue.Unknown(null, 0),
            new SessionSettingValue.Unknown(null, 2),
            new SessionSettingValue.Integer(7),
        ], options.SessionSettings.Select(s => s.Value));
    }

    [Fact]
    public void ReaderParsesOptionsFromHeader()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder().MapOptions("?skiponboarding?ClientIdentity=00ff").ToStream());

        Assert.True(header?.Options.SkippedOnboarding);
        Assert.Single(header!.Options.Options);
    }

    [Fact]
    public void EmptyOptionsParseToNothing()
    {
        var options = SaveMapOptionsParser.Parse("");

        Assert.Empty(options.Options);
        Assert.Empty(options.SessionSettings);
    }
}
