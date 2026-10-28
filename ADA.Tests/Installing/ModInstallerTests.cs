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

    [Fact]
    public void Installs_into_Mods_or_GameFeatures_by_the_uplugin_flag()
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
    public void Leaves_only_the_record_and_lock_in_the_work_folder()
    {
        Installer().Apply([Install(Smod("A", "1.0.0"))]);

        Assert.Equal(["installed.json", "lock"], _game.WorkFolderEntries());
    }

    [Fact]
    public void Records_every_file_with_size_and_hash()
    {
        Installer().Apply([Install(Smod("A", "1.0.0"))]);

        var record = Installer().Installed["A"];
        Assert.Equal("1.0.0", record.Version);
        Assert.Equal(5, record.Files.Count);
        Assert.All(record.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
    }

    [Fact]
    public void Update_replaces_the_whole_folder()
    {
        var v1 = new SmodBuilder("A", "1.0.0").Typical().File("Content/Removed-in-v2.pak", "old").WriteTo(Path.Combine(_archives, "1"));
        var v2 = Smod("A", "2.0.0");
        Installer().Apply([Install(v1)]);

        Installer().Apply([Install(v2)]);

        Assert.Equal(GameFolder.Expected(v2), _game.Read("Mods/A"));
        Assert.Equal("2.0.0", Installer().Installed["A"].Version);
    }

    [Fact]
    public void Update_that_changes_GameFeature_moves_the_mod()
    {
        Installer().Apply([Install(Smod("A", "1.0.0"))]);

        Installer().Apply([Install(Smod("A", "2.0.0", gameFeature: true))]);

        Assert.Null(_game.Read("Mods/A"));
        Assert.NotNull(_game.Read("Mods/GameFeatures/A"));
    }

    [Fact]
    public void Uninstall_removes_the_folder_and_the_record()
    {
        Installer().Apply([Install(Smod("A", "1.0.0", gameFeature: true))]);

        var result = Installer().Apply([new ModChange.Uninstall("A")]);

        Assert.Equal(["A"], result.Uninstalled);
        Assert.Null(_game.Read("Mods/GameFeatures/A"));
        Assert.Empty(Installer().Installed);
    }

    [Fact]
    public void Refuses_while_the_game_is_running()
    {
        _gameRunning = true;

        Assert.Throws<GameRunningException>(() => Installer().Apply([Install(Smod("A", "1.0.0"))]));
        Assert.Null(_game.Read("Mods/A"));
    }

    [Fact]
    public void Never_overwrites_or_removes_a_mod_it_did_not_install()
    {
        _dir.WriteFile("Satisfactory/FactoryGame/Mods/A/A.uplugin", "manual install");

        Assert.Throws<InstallException>(() => Installer().Apply([Install(Smod("A", "1.0.0"))]));
        Assert.Throws<InstallException>(() => Installer().Apply([new ModChange.Uninstall("A")]));
        Assert.Equal("manual install", _game.Read("Mods/A")!["A.uplugin"]);
    }

    [Fact]
    public void A_bad_archive_anywhere_in_the_list_changes_nothing()
    {
        var good = Smod("A", "1.0.0");
        var bad = _dir.WriteFile("Archives/bad.smod", "not a zip");

        Assert.Throws<InvalidModArchiveException>(() => Installer().Apply([Install(good), Install(bad)]));

        Assert.Null(_game.Read("Mods/A"));
        Assert.Empty(Installer().Installed);
    }

    [Fact]
    public void A_failure_part_way_undoes_every_change_in_the_transaction()
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
    public void Cancelling_undoes_everything()
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
    public void Reports_progress_per_file()
    {
        var reports = new List<InstallProgress>();
        Installer().Apply([Install(Smod("A", "1.0.0"))], new SyncProgress<InstallProgress>(reports.Add));

        Assert.Equal(Enumerable.Range(1, 5), reports.Where(r => r.Stage == InstallStage.Extracting).Select(r => r.FilesDone));
        Assert.Equal(InstallStage.Committing, reports[^1].Stage);
    }

    [Fact]
    public void Another_window_holding_the_lock_blocks_changes()
    {
        Directory.CreateDirectory(_game.Work);
        using var held = new FileStream(Path.Combine(_game.Work, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var e = Assert.Throws<InstallException>(() => Installer().Apply([Install(Smod("A", "1.0.0"))]));
        Assert.Contains("Another ADA window", e.Message);
    }

    [Fact]
    public void Check_finds_damage_missing_mods_and_unmanaged_folders()
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
    public void Fast_check_trusts_unchanged_timestamps_and_full_check_hashes_everything()
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
    public void Repair_is_reinstalling_the_same_archive()
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
