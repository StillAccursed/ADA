using ADA.Infrastructure.Saves;
using ADA.Platform.Formats;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Saves;

public class SaveLibraryTests
{
    private const string ACCOUNT = "139504cc558c414395144c448756526e";

    private static readonly SaveLibrary LIBRARY = new(SaveHeaderReader.Read);

    /// <summary>Writes a save under <c>user/Saved/SaveGames/</c>; returns its full path.</summary>
    private static string WriteSave(TempDir dir, string relative, SaveHeaderBuilder? header = null) =>
        WriteBytes(dir, relative, (header ?? new SaveHeaderBuilder()).Build());

    private static string WriteBytes(TempDir dir, string relative, byte[] bytes)
    {
        var path = dir.Combine(["user", "Saved", "SaveGames", .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static long Ticks(int hour) => new DateTime(2026, 10, 25, hour, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public async Task ListsSavesInEachAccountFolder()
    {
        using var dir = new TempDir();
        var client = WriteSave(dir, $"{ACCOUNT}/Atria_autosave_0.sav");
        var server = WriteSave(dir, "server/Dedicated_autosave_0.sav", new SaveHeaderBuilder().SaveName("Dedicated_autosave_0"));

        var contents = await LIBRARY.ReadAsync(dir.Combine("user"));

        Assert.Equal([client, server], contents.Saves.Select(s => s.FilePath).Order(StringComparer.Ordinal));
        Assert.Contains(contents.Saves, s => s.AccountFolder == ACCOUNT && s.Name == "Atria_autosave_2");
        Assert.Contains(contents.Saves, s => s.AccountFolder == "server" && s.Name == "Dedicated_autosave_0");
        Assert.Empty(contents.UnreadableSaves);
    }

    [Fact]
    public async Task LeavesOutFilesTheGameDoesNotLoadAsSaves()
    {
        using var dir = new TempDir();
        var real = WriteSave(dir, $"{ACCOUNT}/Atria_autosave_0.sav");
        WriteSave(dir, "ServerManager_V2.sav");
        WriteSave(dir, "blueprints/Atria/Stray.sav");
        WriteSave(dir, $"{ACCOUNT}/Atria_autosave_0.sav_bak");
        WriteSave(dir, $"{ACCOUNT}/notes.txt");
        var backup = dir.Combine("user", "Saved", "SaveGames_backup", "Verdana_autosave_0.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllBytes(backup, new SaveHeaderBuilder().Build());

        var contents = await LIBRARY.ReadAsync(dir.Combine("user"));

        Assert.Equal(real, Assert.Single(contents.Saves).FilePath);
        Assert.Empty(contents.UnreadableSaves);
    }

    [Fact]
    public async Task ReportsUnreadableSavesSeparately()
    {
        using var dir = new TempDir();
        WriteSave(dir, $"{ACCOUNT}/Good.sav");
        var corrupt = WriteBytes(dir, $"{ACCOUNT}/Corrupt.sav", [1, 2, 3]);

        var contents = await LIBRARY.ReadAsync(dir.Combine("user"));

        Assert.Single(contents.Saves);
        Assert.Equal([corrupt], contents.UnreadableSaves);
    }

    [Fact]
    public async Task NewestSaveTimeComesFirst()
    {
        using var dir = new TempDir();
        WriteSave(dir, $"{ACCOUNT}/Middle.sav", new SaveHeaderBuilder().SaveName("Middle").SavedAtTicks(Ticks(12)));
        WriteSave(dir, $"{ACCOUNT}/Newest.sav", new SaveHeaderBuilder().SaveName("Newest").SavedAtTicks(Ticks(15)));
        WriteSave(dir, $"{ACCOUNT}/Oldest.sav", new SaveHeaderBuilder().SaveName("Oldest").SavedAtTicks(Ticks(9)));

        var contents = await LIBRARY.ReadAsync(dir.Combine("user"));

        Assert.Equal(["Newest", "Middle", "Oldest"], contents.Saves.Select(s => s.Name));
    }

    [Fact]
    public async Task NameFallsBackToFileNameBeforeVersion14()
    {
        using var dir = new TempDir();
        WriteSave(dir, $"{ACCOUNT}/Old Factory.sav", new SaveHeaderBuilder().HeaderVersion(13));

        var contents = await LIBRARY.ReadAsync(dir.Combine("user"));

        Assert.Equal("Old Factory", Assert.Single(contents.Saves).Name);
    }

    [Fact]
    public async Task MissingFolderGivesEmptyContents()
    {
        using var dir = new TempDir();

        var contents = await LIBRARY.ReadAsync(dir.Combine("never-launched"));

        Assert.Empty(contents.Saves);
        Assert.Empty(contents.Blueprints);
        Assert.Empty(contents.Sessions);
    }

    [Fact]
    public async Task ListsBlueprintsWithTheirConfig()
    {
        using var dir = new TempDir();
        var factory = WriteBytes(dir, "blueprints/Atria/Factory.sbp", new byte[100]);
        var factoryConfig = WriteBytes(dir, "blueprints/Atria/Factory.sbpcfg", new byte[20]);
        var loose = WriteBytes(dir, "blueprints/Atria/Loose.sbp", new byte[50]);
        WriteBytes(dir, "blueprints/Atria/BlueprintManager_Data.json", "{}"u8.ToArray());
        WriteBytes(dir, "blueprints/Verdana/Belt.sbp", new byte[10]);

        var contents = await LIBRARY.ReadAsync(dir.Combine("user"));

        Assert.Equal(["Atria/Factory", "Atria/Loose", "Verdana/Belt"], contents.Blueprints.Select(b => $"{b.Session}/{b.Name}"));
        var first = contents.Blueprints[0];
        Assert.Equal(factory, first.BlueprintPath);
        Assert.Equal(factoryConfig, first.ConfigPath);
        Assert.Equal(120, first.Size);
        Assert.Equal(loose, contents.Blueprints[1].BlueprintPath);
        Assert.Null(contents.Blueprints[1].ConfigPath);
    }

    [Fact]
    public async Task SessionsCombineSavesAndBlueprintFolders()
    {
        using var dir = new TempDir();
        WriteSave(dir, $"{ACCOUNT}/Atria_autosave_0.sav");
        WriteBytes(dir, "blueprints/atria/Factory.sbp", [0]);
        WriteBytes(dir, "blueprints/Verdana/Belt.sbp", [0]);

        var contents = await LIBRARY.ReadAsync(dir.Combine("user"));

        Assert.Equal(["Atria", "Verdana"], contents.Sessions);
    }

    [Fact]
    public async Task CancelledReadThrows()
    {
        using var dir = new TempDir();
        WriteSave(dir, $"{ACCOUNT}/Atria_autosave_0.sav");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LIBRARY.ReadAsync(dir.Combine("user"), new CancellationToken(canceled: true)));
    }
}
