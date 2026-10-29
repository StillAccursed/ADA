using System.Security.Cryptography;
using ADA.Core.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Installing;

public sealed class ModInstallerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;
    private readonly string _archives;
    private bool _gameRunning;

    public ModInstallerTests()
    {
        _game = new GameFolder(_dir.Combine("Satisfactory"));
        _archives = _dir.Combine("Archives");
    }

    public void Dispose() => _dir.Dispose();

    private ModInstaller Installer(Action<string>? checkpoint = null) => new(_game.Root, () => _gameRunning, checkpoint);

    private string Smod(string reference, string version, bool gameFeature = false) =>
        new SmodBuilder(reference, version).GameFeature(gameFeature).Typical().WriteTo(Path.Combine(_archives, version));

    private static ModChange.Install Install(string archive) => new(archive, TargetPlatform.Windows);

    private (ModInstaller Installer, WatchedGameFiles Files) Watched(bool readBack)
    {
        var files = new WatchedGameFiles(_game.FactoryGame);
        return (new ModInstaller(files, () => _gameRunning, null, readBack: readBack), files);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsEachFileBackOnlyWhenAskedAndRecordsTheHashWrittenEitherWay(bool readBack)
    {
        var (installer, files) = Watched(readBack);

        installer.Apply([Install(Smod("A", "1.0.0"))]);

        Assert.Equal(readBack, files.Reads.Any(r => r.Contains("/new/", StringComparison.Ordinal)));
        var pak = installer.Installed["A"].Files.Single(f => f.Path.EndsWith(".pak", StringComparison.Ordinal));
        var onDisk = File.ReadAllBytes(_game.ModFolder($"Mods/A/{pak.Path}"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(onDisk)), pak.Sha256);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFileShorterOnDiskThanWrittenFailsTheInstall(bool readBack)
    {
        var (installer, files) = Watched(readBack);
        files.ShortenEnding = ".pak";

        var e = Assert.Throws<InstallException>(() => installer.Apply([Install(Smod("A", "1.0.0"))]));

        Assert.Contains("bytes on disk, not the", e.Message);
        Assert.Null(_game.Read("Mods/A"));
    }

    [Fact]
    public void InstallsIntoModsOrGameFeaturesByTheUpluginFlag()
    {
        var plain = Smod("SML", "3.12.0");
        var feature = Smod("WeatherExtended", "1.1.0", gameFeature: true);

        var result = Installer().Apply([Install(plain), Install(feature)]);

        Assert.Equal(GameFolder.Expected(plain), _game.Read("Mods/SML"));
        Assert.Equal(GameFolder.Expected(feature), _game.Read("Mods/GameFeatures/WeatherExtended"));
        Assert.Equal(["SML", "WeatherExtended"], result.Installed.Select(m => m.Reference));
        Assert.Equal("Mods/GameFeatures/WeatherExtended", Installer().Installed["WeatherExtended"].Location);
    }

    [Fact]
    public void LeavesOnlyTheRecordAndLockInTheWorkFolder()
    {
        Installer().Apply([Install(Smod("A", "1.0.0"))]);

        Assert.Equal(["installed.json", "lock"], _game.WorkFolderEntries());
    }

    [Fact]
    public void RecordsEveryFileWithSizeAndHash()
    {
        Installer().Apply([Install(Smod("A", "1.0.0"))]);

        var record = Installer().Installed["A"];
        Assert.Equal("1.0.0", record.Version);
        Assert.Equal(5, record.Files.Count);
        Assert.All(record.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
    }

    [Fact]
    public void UpdateReplacesTheWholeFolder()
    {
        var v1 = new SmodBuilder("A", "1.0.0").Typical().File("Content/Removed-in-v2.pak", "old").WriteTo(Path.Combine(_archives, "1"));
        var v2 = Smod("A", "2.0.0");
        Installer().Apply([Install(v1)]);

        Installer().Apply([Install(v2)]);

        Assert.Equal(GameFolder.Expected(v2), _game.Read("Mods/A"));
        Assert.Equal("2.0.0", Installer().Installed["A"].Version);
    }

    [Fact]
    public void UpdateThatChangesGameFeatureMovesTheMod()
    {
        Installer().Apply([Install(Smod("A", "1.0.0"))]);

        Installer().Apply([Install(Smod("A", "2.0.0", gameFeature: true))]);

        Assert.Null(_game.Read("Mods/A"));
        Assert.NotNull(_game.Read("Mods/GameFeatures/A"));
    }

    [Fact]
    public void UninstallRemovesTheFolderAndTheRecord()
    {
        Installer().Apply([Install(Smod("A", "1.0.0", gameFeature: true))]);

        var result = Installer().Apply([new ModChange.Uninstall("A")]);

        Assert.Equal(["A"], result.Uninstalled);
        Assert.Null(_game.Read("Mods/GameFeatures/A"));
        Assert.Empty(Installer().Installed);
    }

    [Fact]
    public void RefusesWhileTheGameIsRunning()
    {
        _gameRunning = true;

        Assert.Throws<GameRunningException>(() => Installer().Apply([Install(Smod("A", "1.0.0"))]));
        Assert.Null(_game.Read("Mods/A"));
    }

    [Fact]
    public void NeverOverwritesOrRemovesAModItDidNotInstall()
    {
        _dir.WriteFile("Satisfactory/FactoryGame/Mods/A/A.uplugin", "manual install");

        Assert.Throws<InstallException>(() => Installer().Apply([Install(Smod("A", "1.0.0"))]));
        Assert.Throws<InstallException>(() => Installer().Apply([new ModChange.Uninstall("A")]));
        Assert.Equal("manual install", _game.Read("Mods/A")!["A.uplugin"]);
    }

    [Fact]
    public void ABadArchiveAnywhereInTheListChangesNothing()
    {
        var good = Smod("A", "1.0.0");
        var bad = _dir.WriteFile("Archives/bad.smod", "not a zip");

        Assert.Throws<InvalidModArchiveException>(() => Installer().Apply([Install(good), Install(bad)]));

        Assert.Null(_game.Read("Mods/A"));
        Assert.Empty(Installer().Installed);
    }

    [Fact]
    public void AFailurePartWayUndoesEveryChangeInTheTransaction()
    {
        var a1 = Smod("A", "1.0.0");
        var b1 = Smod("B", "1.0.0");
        Installer().Apply([Install(a1), Install(b1)]);

        // A updates, B is removed, C would be new; then moving C into Mods fails.
        var failing = Installer(cp =>
        {
            if (cp == "moved-in:C")
            {
                throw new IOException("disk said no");
            }
        });
        var e = Assert.Throws<InstallException>(() =>
            failing.Apply([Install(Smod("A", "2.0.0")), new ModChange.Uninstall("B"), Install(Smod("C", "1.0.0"))]));

        Assert.Contains("disk said no", e.Message);
        Assert.Equal(GameFolder.Expected(a1), _game.Read("Mods/A"));
        Assert.Equal(GameFolder.Expected(b1), _game.Read("Mods/B"));
        Assert.Null(_game.Read("Mods/C"));
        Assert.Equal(["1.0.0", "1.0.0"], Installer().Installed.Values.OrderBy(m => m.Reference).Select(m => m.Version));
        Assert.Equal(["installed.json", "lock"], _game.WorkFolderEntries());
    }

    [Fact]
    public void CancellingUndoesEverything()
    {
        using var cts = new CancellationTokenSource();
        var installer = Installer(cp =>
        {
            if (cp == "extracted:B:1")
            {
                cts.Cancel();
            }
        });

        var e = Assert.Throws<InstallException>(() =>
            installer.Apply([Install(Smod("A", "1.0.0")), Install(Smod("B", "1.0.0"))], ct: cts.Token));

        Assert.Contains("Cancelled", e.Message);
        Assert.Null(_game.Read("Mods/A"));
        Assert.Null(_game.Read("Mods/B"));
    }

    [Fact]
    public void ReportsProgressPerFile()
    {
        var reports = new List<InstallProgress>();
        Installer().Apply([Install(Smod("A", "1.0.0"))], new SyncProgress<InstallProgress>(reports.Add));

        Assert.Equal(Enumerable.Range(1, 5), reports.Where(r => r.Stage == InstallStage.Extracting).Select(r => r.FilesDone));
        Assert.Equal(InstallStage.Committing, reports[^1].Stage);
    }

    [Fact]
    public void AnotherWindowHoldingTheLockBlocksChanges()
    {
        Directory.CreateDirectory(_game.Work);
        using var held = new FileStream(Path.Combine(_game.Work, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var e = Assert.Throws<InstallException>(() => Installer().Apply([Install(Smod("A", "1.0.0"))]));
        Assert.Contains("Another ADA window", e.Message);
    }

    [Fact]
    public void CheckFindsDamageMissingModsAndUnmanagedFolders()
    {
        Installer().Apply([Install(Smod("A", "1.0.0")), Install(Smod("B", "1.0.0")), Install(Smod("C", "1.0.0", gameFeature: true))]);
        File.WriteAllText(Path.Combine(_game.Mods, "A", "Resources", "Icon128.png"), "replaced by hand");
        Directory.Delete(Path.Combine(_game.Mods, "B"), recursive: true);
        _dir.WriteFile("Satisfactory/FactoryGame/Mods/Manual/Manual.uplugin", "{}");
        _dir.WriteFile("Satisfactory/FactoryGame/Mods/GameFeatures/OtherTool/OtherTool.uplugin", "{}");

        var check = Installer().Check();

        Assert.Equal(ModHealth.Damaged, check.Mods.Single(m => m.Reference == "A").Health);
        Assert.Equal(ModHealth.Missing, check.Mods.Single(m => m.Reference == "B").Health);
        Assert.Equal(ModHealth.Ok, check.Mods.Single(m => m.Reference == "C").Health);
        Assert.Equal(["Mods/GameFeatures/OtherTool", "Mods/Manual"], check.Unmanaged);
        Assert.Equal(["A", "B"], check.NeedingRepair.Select(m => m.Reference));
    }

    [Fact]
    public void FastCheckTrustsUnchangedTimestampsAndFullCheckHashesEverything()
    {
        Installer().Apply([Install(Smod("A", "1.0.0"))]);
        var icon = Path.Combine(_game.Mods, "A", "Resources", "Icon128.png");
        var stamp = File.GetLastWriteTimeUtc(icon);
        File.WriteAllText(icon, "png B"); // same length as "png A", content differs
        File.SetLastWriteTimeUtc(icon, stamp);

        Assert.Equal(ModHealth.Ok, Installer().Check().Mods.Single().Health);
        Assert.Equal(ModHealth.Damaged, Installer().Check(full: true).Mods.Single().Health);
    }

    [Fact]
    public void RepairIsReinstallingTheSameArchive()
    {
        var a = Smod("A", "1.0.0");
        Installer().Apply([Install(a)]);
        File.Delete(Path.Combine(_game.Mods, "A", "Resources", "Icon128.png"));

        Installer().Apply([Install(a)]);

        Assert.Equal(GameFolder.Expected(a), _game.Read("Mods/A"));
        Assert.Empty(Installer().Check(full: true).NeedingRepair);
    }

    /// <summary>Reports synchronously, unlike <see cref="Progress{T}"/>, so tests see every report.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
