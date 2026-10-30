using System.IO.Compression;
using System.Text;
using ADA.Core.Saves;
using ADA.Infrastructure.Saves;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Saves;

public sealed class BlueprintTransferTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly BlueprintTransfer _transfer = new();

    public void Dispose() => _dir.Dispose();

    private string UserData => _dir.Combine("user");

    private string Folder(string session) => BlueprintTransfer.SessionFolder(UserData, session);

    /// <summary>Writes a blueprint whose files hold <paramref name="content"/>, so tests can tell copies apart.</summary>
    private Blueprint Write(string session, string name, string content = "sbp", string? config = "cfg")
    {
        var folder = Directory.CreateDirectory(Folder(session)).FullName;
        File.WriteAllText(Path.Combine(folder, name + ".sbp"), content);
        if (config is not null)
        {
            File.WriteAllText(Path.Combine(folder, name + ".sbpcfg"), config);
        }
        return new Blueprint(name, session, Path.Combine(folder, name + ".sbp"),
            config is null ? null : Path.Combine(folder, name + ".sbpcfg"), 0, DateTime.UtcNow);
    }

    private string Read(string session, string file) => File.ReadAllText(Path.Combine(Folder(session), file));

    private async Task<BlueprintWriteResult> CopyAsync(string to, BlueprintClash clash, params Blueprint[] blueprints) =>
        await _transfer.WriteAsync(UserData, to, await _transfer.ReadAsync(blueprints), clash);

    private string Zip(params (string Entry, string Content)[] entries)
    {
        var path = _dir.Combine($"import-{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var stream = archive.CreateEntry(entry).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    [Fact]
    public async Task CopiesBothFilesIntoANewSessionFolder()
    {
        var factory = Write("Atria", "Factory", "A-sbp", "A-cfg");

        var result = await CopyAsync("Verdana", BlueprintClash.KeepBoth, factory);

        Assert.Equal(["Factory"], result.Written);
        Assert.Equal("A-sbp", Read("Verdana", "Factory.sbp"));
        Assert.Equal("A-cfg", Read("Verdana", "Factory.sbpcfg"));
        Assert.Empty(Directory.GetFiles(Folder("Verdana"), "*.tmp"));
    }

    [Fact]
    public async Task CopiesABlueprintWithoutConfig()
    {
        var loose = Write("Atria", "Loose", config: null);

        await CopyAsync("Verdana", BlueprintClash.KeepBoth, loose);

        Assert.True(File.Exists(Path.Combine(Folder("Verdana"), "Loose.sbp")));
        Assert.False(File.Exists(Path.Combine(Folder("Verdana"), "Loose.sbpcfg")));
    }

    [Fact]
    public async Task KeepBothNumbersTheIncomingCopy()
    {
        Write("Verdana", "Factory", "old");
        Write("Verdana", "Factory (2)", "older");
        var incoming = Write("Atria", "factory", "new");

        var result = await CopyAsync("Verdana", BlueprintClash.KeepBoth, incoming);

        Assert.Equal([("factory", "factory (3)")], result.Renamed);
        Assert.Equal("old", Read("Verdana", "Factory.sbp"));
        Assert.Equal("new", Read("Verdana", "factory (3).sbp"));
    }

    [Fact]
    public async Task ReplaceKeepsTheExistingCasingAndDropsAStaleConfig()
    {
        Write("Verdana", "Factory", "old", "old-cfg");
        var incoming = Write("Atria", "factory", "new", config: null);

        await CopyAsync("Verdana", BlueprintClash.Replace, incoming);

        Assert.Equal(["Factory.sbp"], Directory.GetFiles(Folder("Verdana")).Select(Path.GetFileName));
        Assert.Equal("new", Read("Verdana", "Factory.sbp"));
    }

    [Fact]
    public void ClashesIgnoreCase()
    {
        Write("Verdana", "Factory");

        Assert.Equal(["FACTORY"], _transfer.Clashes(UserData, "Verdana", ["FACTORY", "Belt"]));
        Assert.Empty(_transfer.Clashes(UserData, "Nowhere", ["Factory"]));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData("trailing.")]
    public void RefusesSessionNamesThatAreNotFolderNames(string session)
    {
        Assert.Throws<ArgumentException>(() => BlueprintTransfer.SessionFolder(UserData, session));
    }

    [Fact]
    public async Task ExportThenImportRoundTrips()
    {
        var factory = Write("Atria", "Factory", "A-sbp", "A-cfg");
        var loose = Write("Atria", "Loose", "L-sbp", config: null);
        var zip = _dir.Combine("export.zip");
        await using (var stream = File.Create(zip))
        {
            await _transfer.ExportAsync(await _transfer.ReadAsync([factory, loose]), stream);
        }

        var import = await _transfer.ReadImportAsync([zip]);

        Assert.Empty(import.Problems);
        Assert.Collection(import.Blueprints,
            b =>
            {
                Assert.Equal("Factory", b.Name);
                Assert.Equal("A-cfg", Encoding.UTF8.GetString(b.Config!));
            },
            b =>
            {
                Assert.Equal("Loose", b.Name);
                Assert.Null(b.Config);
            });
    }

    [Fact]
    public async Task ImportReadsEntriesByFileNameOnly()
    {
        var zip = Zip(("pack/Factory.sbp", "sbp"), ("pack/Factory.sbpcfg", "cfg"), ("../../Evil.sbp", "evil"),
            (@"win\Belt.sbp", "belt"), ("readme.txt", "hi"));

        var import = await _transfer.ReadImportAsync([zip]);
        await _transfer.WriteAsync(UserData, "Atria", import.Blueprints, BlueprintClash.KeepBoth);

        Assert.Equal(["Belt", "Evil", "Factory"], import.Blueprints.Select(b => b.Name));
        Assert.Empty(import.Problems);
        Assert.True(File.Exists(Path.Combine(Folder("Atria"), "Evil.sbp")));
        Assert.Equal(["Saved"], Directory.GetDirectories(UserData).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ImportListsWhatItLeftOut()
    {
        var zip = Zip(("Bad:Name.sbp", "x"), ("Orphan.sbpcfg", "x"), ("Twice.sbp", "1"));
        var second = Zip(("Twice.sbp", "2"));

        var import = await _transfer.ReadImportAsync([zip, second, _dir.WriteFile("notes.txt", "x")]);

        Assert.Equal(["Twice"], import.Blueprints.Select(b => b.Name));
        Assert.Equal("1", Encoding.UTF8.GetString(import.Blueprints[0].Blueprint));
        Assert.Equal(4, import.Problems.Count);
        Assert.Contains(import.Problems, p => p.StartsWith("Bad:Name.sbp has a name"));
        Assert.Contains(import.Problems, p => p.StartsWith("Orphan.sbpcfg has no .sbp"));
        Assert.Contains(import.Problems, p => p.StartsWith("Twice.sbp was chosen more than once"));
        Assert.Contains(import.Problems, p => p.StartsWith("notes.txt isn't a blueprint"));
    }

    [Fact]
    public async Task ImportPicksUpTheConfigBesideALooseBlueprint()
    {
        var sbp = _dir.WriteFile("downloads/Factory.sbp", "sbp");
        _dir.WriteFile("downloads/Factory.sbpcfg", "cfg");

        var import = await _transfer.ReadImportAsync([sbp]);

        Assert.Equal("cfg", Encoding.UTF8.GetString(Assert.Single(import.Blueprints).Config!));
    }

    [Fact]
    public async Task ImportLeavesOutFilesOverTheLimitWhateverTheirHeaderSays()
    {
        var small = new BlueprintTransfer(maxFileBytes: 4);
        var zip = Zip(("Big.sbp", "too large"), ("Ok.sbp", "ok"));

        var import = await small.ReadImportAsync([zip]);

        Assert.Equal(["Ok"], import.Blueprints.Select(b => b.Name));
        Assert.Contains(import.Problems, p => p.StartsWith("Big.sbp is larger than"));
    }
}
