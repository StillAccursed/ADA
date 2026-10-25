using System.Text.Json.Nodes;
using Overlord.Core.Catalog;
using Overlord.Infrastructure.Installing;
using Overlord.Tests.Fixtures;

namespace Overlord.Tests.Installing;

public sealed class TransactionCleanupTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameFolder _game;

    public TransactionCleanupTests()
    {
        _game = new GameFolder(_dir.Combine("Satisfactory"));
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(false, "cleanup-started", false)]
    [InlineData(false, "work-cleaned", false)]
    [InlineData(true, "cleanup-started", false)]
    [InlineData(true, "work-cleaned", false)]
    [InlineData(false, "work-cleaned", true)]
    [InlineData(true, "work-cleaned", true)]
    public void CleanupFailureKeepsTheCommittedUpdateAndRecoveryOnlyRetriesCleanup(
        bool gameFeature, string failureCheckpoint, bool accessDenied)
    {
        var oldArchive = Archive("A", "1.0.0");
        var newArchive = Archive("A", "2.0.0", gameFeature);
        Installer().Apply([Install(oldArchive)]);
        var checkpoints = new List<string>();

        var result = Installer(point =>
        {
            checkpoints.Add(point);
            if (point == failureCheckpoint)
            {
                Assert.True(JournalFromDisk().Committed);
                if (accessDenied)
                {
                    throw new UnauthorizedAccessException("cleanup denied");
                }
                throw new IOException("cleanup failed");
            }
        }).Apply([Install(newArchive)]);

        Assert.Equal("2.0.0", Assert.Single(result.Installed).Version);
        AssertCommittedUpdate(newArchive, gameFeature);
        Assert.DoesNotContain("rollback-started", checkpoints);
        Assert.True(Installer().HasInterruptedTransaction);
        var journal = JournalFromDisk();
        Assert.True(journal.Committed);
        Assert.Equal(failureCheckpoint == "cleanup-started", Directory.Exists(
            Path.Combine(_game.Work, "txn-" + journal.TransactionId, "old", "A")));

        // Repeated cleanup failure must retain the marker and leave the new version installed.
        Assert.Throws<IOException>(() => Installer(point =>
        {
            if (point == "cleanup-started")
            {
                throw new IOException("still unavailable");
            }
        }).Recover());
        Assert.True(JournalFromDisk().Committed);
        AssertCommittedUpdate(newArchive, gameFeature);

        var recoveryCheckpoints = new List<string>();
        var recovery = Installer(recoveryCheckpoints.Add).Recover();

        Assert.True(recovery.HadInterruptedTransaction);
        Assert.Equal(["A"], recovery.RolledForward);
        Assert.Empty(recovery.RolledBack);
        Assert.DoesNotContain(recoveryCheckpoints, point => point.StartsWith("recorded:", StringComparison.Ordinal));
        AssertCommittedUpdate(newArchive, gameFeature);
        Assert.Equal(["installed.json", "lock"], _game.WorkFolderEntries());
        Assert.False(Installer().Recover().HadInterruptedTransaction);
    }

    [Fact]
    public void UninstallCleanupFailureKeepsTheModRemoved()
    {
        Installer().Apply([Install(Archive("A", "1.0.0"))]);

        var result = Installer(point =>
        {
            if (point == "work-cleaned")
            {
                throw new IOException("journal cleanup failed after removing the backup");
            }
        }).Apply([new ModChange.Uninstall("A")]);

        Assert.Equal(["A"], result.Uninstalled);
        Assert.Empty(Installer().Installed);
        Assert.Null(_game.Read("Mods/A"));
        Assert.True(JournalFromDisk().Committed);

        Installer().Recover();

        Assert.Empty(Installer().Installed);
        Assert.Null(_game.Read("Mods/A"));
        Assert.Equal(["installed.json", "lock"], _game.WorkFolderEntries());
    }

    [Fact]
    public void PartlyDeletedBackupDoesNotTriggerRollback()
    {
        Installer().Apply([Install(Archive("A", "1.0.0"))]);
        var newArchive = Archive("A", "2.0.0");

        Installer(point =>
        {
            if (point == "cleanup-started")
            {
                var journal = JournalFromDisk();
                Assert.True(journal.Committed);
                var backup = Path.Combine(_game.Work, "txn-" + journal.TransactionId, "old", "A");
                File.Delete(Path.Combine(backup, "A.uplugin"));
                throw new IOException("cleanup stopped after deleting one old file");
            }
        }).Apply([Install(newArchive)]);

        AssertCommittedUpdate(newArchive, gameFeature: false);
        Installer().Recover();
        AssertCommittedUpdate(newArchive, gameFeature: false);
        Assert.Equal(["installed.json", "lock"], _game.WorkFolderEntries());
    }

    [Fact]
    public void NewTransactionWaitsUntilPreviousCleanupCanFinish()
    {
        var newArchive = Archive("A", "2.0.0");
        var nextArchive = Archive("B", "1.0.0");
        var failing = Installer(point =>
        {
            if (point == "cleanup-started")
            {
                throw new IOException("cleanup unavailable");
            }
        });
        failing.Apply([Install(newArchive)]);
        var pending = JournalFromDisk().TransactionId;

        Assert.Throws<IOException>(() => failing.Apply([Install(nextArchive)]));

        Assert.Equal(pending, JournalFromDisk().TransactionId);
        AssertCommittedUpdate(newArchive, gameFeature: false);
        Assert.Null(_game.Read("Mods/B"));

        Installer().Apply([Install(nextArchive)]);

        Assert.Equal(GameFolder.Expected(nextArchive), _game.Read("Mods/B"));
        AssertCommittedUpdate(newArchive, gameFeature: false);
        Assert.Equal(["installed.json", "lock"], _game.WorkFolderEntries());
    }

    [Fact]
    public void RecoveryOfAnOlderJournalWithoutACommittedMarkerIsPreserved()
    {
        var archive = Archive("A", "1.0.0");
        Assert.Throws<SimulatedCrashException>(() => Installer(point =>
        {
            if (point == "ready:A")
            {
                throw new SimulatedCrashException(point);
            }
        }).Apply([Install(archive)]));
        var path = Path.Combine(_game.Work, "journal.json");
        var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(legacy.Remove("committed"));
        File.WriteAllText(path, legacy.ToJsonString());

        Assert.False(JournalFromDisk().Committed);
        var recovery = Installer().Recover();

        Assert.Equal(["A"], recovery.RolledForward);
        Assert.Equal("1.0.0", Installer().Installed["A"].Version);
        Assert.Equal(GameFolder.Expected(archive), _game.Read("Mods/A"));
        Assert.Equal(["installed.json", "lock"], _game.WorkFolderEntries());
    }

    private ModInstaller Installer(Action<string>? checkpoint = null) => new(_game.Root, () => false, checkpoint);

    private string Archive(string reference, string version, bool gameFeature = false) =>
        new SmodBuilder(reference, version).GameFeature(gameFeature).Typical()
            .WriteTo(_dir.Combine("Archives", reference, version));

    private static ModChange.Install Install(string archive) => new(archive, TargetPlatform.Windows);

    private Journal JournalFromDisk() => new JournalStore(Path.Combine(_game.Work, "journal.json")).Load()!;

    private void AssertCommittedUpdate(string archive, bool gameFeature)
    {
        var location = gameFeature ? "Mods/GameFeatures/A" : "Mods/A";
        var otherLocation = gameFeature ? "Mods/A" : "Mods/GameFeatures/A";
        Assert.Equal(GameFolder.Expected(archive), _game.Read(location));
        Assert.Null(_game.Read(otherLocation));
        Assert.Equal("2.0.0", Installer().Installed["A"].Version);
        Assert.Empty(Installer().Check(full: true).NeedingRepair);
    }
}
