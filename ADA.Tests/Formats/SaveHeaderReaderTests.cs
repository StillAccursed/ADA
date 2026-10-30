using System.Text;
using ADA.Core.Saves;
using ADA.Platform.Formats;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Formats;

public class SaveHeaderReaderTests
{
    // Shape copied from a real 1.2 save's mod metadata.
    private const string MOD_METADATA = """
        {"Version":1,"Mods":[
          {"Reference":"ExtraSwatchSlots","Name":"64 Extra Swatch Slots","Version":"1.0.2"},
          {"Reference":"Achievements","Name":"Achievements","Version":"2.0.7"}
        ]}
        """;

    [Fact]
    public void ReadsVersion14Header()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder().ToStream());

        Assert.NotNull(header);
        Assert.Equal(14, header.HeaderVersion);
        Assert.Equal(60, header.SaveVersion);
        Assert.Equal(513885, header.BuildVersion);
        Assert.Equal("Atria_autosave_2", header.SaveName);
        Assert.Equal("Persistent_Level", header.MapName);
        Assert.Equal("Atria", header.SessionName);
        Assert.Equal(TimeSpan.FromMinutes(10), header.PlayTime);
        Assert.Equal(new DateTime(2026, 10, 25, 12, 53, 12, DateTimeKind.Utc), header.SavedAtUtc);
        Assert.Equal(DateTimeKind.Utc, header.SavedAtUtc.Kind);
        Assert.Equal(SessionVisibility.Private, header.Visibility);
        Assert.False(header.IsModded);
        Assert.Empty(header.Mods);
    }

    [Fact]
    public void ReadsVersion14Tail()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder().AdvancedGameSettings().ToStream());

        Assert.NotNull(header);
        Assert.Equal("0bV9f0YUAB7qx-SKOn2I2Q", header.SaveIdentifier);
        Assert.True(header.IsPartitionedWorld);
        Assert.True(header.AdvancedGameSettings);
    }

    [Fact]
    public void ReadsFriendsOnlyVisibility()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder().Visibility(1).ToStream());

        Assert.Equal(SessionVisibility.FriendsOnly, header?.Visibility);
    }

    [Fact]
    public void ReadsModsRecordedInSave()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder().ModMetadata(MOD_METADATA).ToStream());

        Assert.NotNull(header);
        Assert.True(header.IsModded);
        Assert.Collection(header.Mods,
            mod =>
            {
                Assert.Equal("ExtraSwatchSlots", mod.Reference);
                Assert.Equal("64 Extra Swatch Slots", mod.Name);
                Assert.Equal("1.0.2", mod.Version);
            },
            mod => Assert.Equal("Achievements", mod.Reference));
    }

    [Fact]
    public void FallsBackToReferenceWhenModHasNoName()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder()
            .ModMetadata("""{"Mods":[{"Reference":"SML"},{"Name":"No reference"}]}""").ToStream());

        var mod = Assert.Single(header!.Mods);
        Assert.Equal("SML", mod.Name);
        Assert.Null(mod.Version);
    }

    [Fact]
    public void KeepsHeaderWhenModListIsUnreadable()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder().ModMetadata("not json").ToStream());

        Assert.NotNull(header);
        Assert.True(header.IsModded);
        Assert.Empty(header.Mods);
    }

    [Fact]
    public void Version13SaveHasNoSaveNameOrTail()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder().HeaderVersion(13).ToStream());

        Assert.NotNull(header);
        Assert.Null(header.SaveName);
        Assert.Equal("Atria", header.SessionName);
        Assert.Null(header.SaveIdentifier);
        Assert.Null(header.IsPartitionedWorld);
        Assert.Null(header.AdvancedGameSettings);
    }

    [Fact]
    public void ReadsNewerHeaderVersionsWithoutTail()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder().HeaderVersion(15).ToStream());

        Assert.Equal("Atria", header?.SessionName);
        Assert.Null(header?.AdvancedGameSettings);
    }

    [Fact]
    public void ReadsUtf16Strings()
    {
        var header = SaveHeaderReader.Read(new SaveHeaderBuilder()
            .Utf16Strings().Session("Fábrica 工厂").SaveName("Fábrica 工厂_autosave_0").ToStream());

        Assert.Equal("Fábrica 工厂", header?.SessionName);
        Assert.Equal("Fábrica 工厂_autosave_0", header?.SaveName);
    }

    [Fact]
    public void StopsBeforeWorldData()
    {
        var world = Enumerable.Repeat((byte)0xFF, 4096).ToArray();
        using var stream = new SaveHeaderBuilder().WorldData(world).ToStream();

        var header = SaveHeaderReader.Read(stream);

        Assert.NotNull(header);
        Assert.Equal(stream.Length - world.Length, stream.Position);
    }

    [Fact]
    public void ReturnsNullForServerManagerFile()
    {
        var bytes = Encoding.ASCII.GetBytes("MSGF").Concat(new byte[64]).ToArray();

        Assert.Null(SaveHeaderReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void ReturnsNullForHeadersBeforeUpdate5()
    {
        Assert.Null(SaveHeaderReader.Read(new SaveHeaderBuilder().HeaderVersion(7).ToStream()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(40)]
    public void ReturnsNullForTruncatedHeader(int length)
    {
        var bytes = new SaveHeaderBuilder().ModMetadata(MOD_METADATA).Build()[..length];

        Assert.Null(SaveHeaderReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void ReturnsNullForImplausibleStringLength()
    {
        var bytes = new SaveHeaderBuilder().Build();
        BitConverter.GetBytes(100 * 1024 * 1024).CopyTo(bytes, 12); // The save name's length.

        Assert.Null(SaveHeaderReader.Read(new MemoryStream(bytes)));
    }

    [Theory]
    [InlineData("", 600, 1L)]
    [InlineData("Atria", -1, 1L)]
    [InlineData("Atria", 600, 0L)]
    public void ReturnsNullWhenFieldsFailSanityChecks(string session, int playSeconds, long ticks)
    {
        var bytes = new SaveHeaderBuilder().Session(session).PlaySeconds(playSeconds).SavedAtTicks(ticks).Build();

        Assert.Null(SaveHeaderReader.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void ReadsSaveFromDisk()
    {
        using var dir = new TempDir();
        var path = dir.Combine("Atria_autosave_2.sav");
        File.WriteAllBytes(path, new SaveHeaderBuilder().Build());

        Assert.Equal("Atria", SaveHeaderReader.Read(path)?.SessionName);
    }

    [Fact]
    public void ReturnsNullWhenFileIsMissing()
    {
        using var dir = new TempDir();

        Assert.Null(SaveHeaderReader.Read(dir.Combine("missing.sav")));
    }
}
