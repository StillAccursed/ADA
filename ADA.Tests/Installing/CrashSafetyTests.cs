using ADA.Core.Catalog;
using ADA.Infrastructure.Installing;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Installing;

/// <summary>
/// Fault injection: run a transaction, kill it at every single step, recover, and check that every
/// mod is wholly in its old state or wholly in its new state, and that the record agrees with Mods.
/// </summary>
public sealed class CrashSafetyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>One fresh game per run, all built from the same archives.</summary>
    private sealed class Scenario
    {
        public required GameFolder Game { get; init; }
        public required IReadOnlyList<ModChange> Transaction { get; init; }

        /// <summary>Mod → the folder contents it may end up with (null = absent), keyed by location.</summary>
        public required Dictionary<string, (string Location, Dictionary<string, string>? Content, string? Version)[]> Allowed { get; init; }

        public ModInstaller Installer(Action<string>? checkpoint = null) => new(Game.Root, () => false, checkpoint);
    }

    private int _runs;

    private Scenario NewScenario()
    {
        var archives = _dir.Combine("Archives");
        string Smod(string reference, string version, bool gameFeature) =>
            File.Exists(Path.Combine(archives, version + (gameFeature ? "-gf" : ""), $"{reference}-{version}.smod"))
                ? Path.Combine(archives, version + (gameFeature ? "-gf" : ""), $"{reference}-{version}.smod")
                : new SmodBuilder(reference, version).GameFeature(gameFeature).Typical()
                    .File($"Content/Only-in-{version}.pak", version)
                    .WriteTo(Path.Combine(archives, version + (gameFeature ? "-gf" : "")));

        var a1 = Smod("A", "1.0.0", false);
        var a2 = Smod("A", "2.0.0", true); // update that also moves A into GameFeatures
        var b1 = Smod("B", "1.0.0", false);
        var c1 = Smod("C", "1.0.0", true);

        var game = new GameFolder(_dir.Combine($"Game{++_runs}"));
        new ModInstaller(game.Root, () => false).Apply(
            [new ModChange.Install(a1, TargetPlatform.Windows), new ModChange.Install(b1, TargetPlatform.Windows)]);

        return new Scenario
        {
            Game = game,
            Transaction =
            [
                new ModChange.Install(a2, TargetPlatform.Windows),
                new ModChange.Uninstall("B"),
                new ModChange.Install(c1, TargetPlatform.Windows),
            ],
            Allowed = new()
            {
                ["A"] = [("Mods/A", GameFolder.Expected(a1), "1.0.0"), ("Mods/GameFeatures/A", GameFolder.Expected(a2), "2.0.0")],
                ["B"] = [("Mods/B", GameFolder.Expected(b1), "1.0.0"), ("Mods/B", null, null)],
                ["C"] = [("Mods/GameFeatures/C", null, null), ("Mods/GameFeatures/C", GameFolder.Expected(c1), "1.0.0")],
            },
        };
    }

    /// <summary>Every checkpoint a clean run passes through, in order.</summary>
    private List<string> CleanRunCheckpoints(Func<Scenario, ModInstaller, Action> run)
    {
        var points = new List<string>();
        var scenario = NewScenario();
        run(scenario, scenario.Installer(points.Add))();
        return points;
    }

    private static Action<string> CrashAt(int n)
    {
        var seen = 0;
        return point =>
        {
            if (++seen == n)
            {
                throw new SimulatedCrashException(point);
            }
        };
    }

    /// <summary>The invariant: each mod wholly old or wholly new, record matching Mods, nothing left over.</summary>
    private static void AssertConsistent(Scenario s, string context)
    {
        var installed = s.Installer().Installed;
        foreach (var (reference, options) in s.Allowed)
        {
            var matching = options.Where(o =>
            {
                // Every location this mod could occupy must hold exactly what this option says.
                var here = s.Game.Read(o.Location);
                var elsewhere = options.Select(x => x.Location).Distinct().Where(l => l != o.Location).Select(s.Game.Read);
                return Same(here, o.Content) && elsewhere.All(e => e is null);
            }).ToList();

            Assert.True(matching.Count > 0, $"{context}: {reference} is in a mixed or partial state.");
            var expectedVersion = matching[0].Version;
            Assert.True(installed.GetValueOrDefault(reference)?.Version == expectedVersion,
                $"{context}: record says {reference} {installed.GetValueOrDefault(reference)?.Version ?? "absent"}, Mods has {expectedVersion ?? "nothing"}.");
        }

        Assert.False(s.Installer().HasInterruptedTransaction, $"{context}: journal left behind.");
        Assert.DoesNotContain(s.Game.WorkFolderEntries(), e => e.StartsWith("txn-"));
        var check = s.Installer().Check(full: true);
        Assert.True(!check.NeedingRepair.Any(), $"{context}: check found {string.Join(", ", check.NeedingRepair.Select(m => m.Reference))}.");
        Assert.Empty(check.Unmanaged);
    }

    private static bool Same(Dictionary<string, string>? actual, Dictionary<string, string>? expected) =>
        actual is null || expected is null
            ? actual is null && expected is null
            : actual.Count == expected.Count && actual.All(kv => expected.TryGetValue(kv.Key, out var v) && v == kv.Value);

    [Fact]
    public void CrashAtAnyStepThenRecoveryLeavesEveryModWhole()
    {
        var points = CleanRunCheckpoints((s, installer) => () => installer.Apply(s.Transaction));
        // Every kind of step is covered, for an update, an uninstall and a fresh install.
        Assert.Subset(points.ToHashSet(), new HashSet<string>
        {
            "journal-created", "extracted:A:0", "ready:A", "moved-out:A", "moved-in:A", "recorded:A", "done:A",
            "moved-out:B", "recorded:B", "done:B", "extracted:C:5", "ready:C", "moved-in:C", "done:C",
            "work-cleaned", "journal-removed",
        });

        for (var n = 1; n <= points.Count; n++)
        {
            var s = NewScenario();
            Assert.Throws<SimulatedCrashException>(() => s.Installer(CrashAt(n)).Apply(s.Transaction));

            var recovery = s.Installer().Recover();

            AssertConsistent(s, $"crash at #{n} ({points[n - 1]})");
            // Only a crash after the journal is gone leaves nothing to recover.
            Assert.Equal(points[n - 1] != "journal-removed", recovery.HadInterruptedTransaction);
        }
    }

    [Fact]
    public void CrashDuringRecoveryIsRecoveredOnTheNextLaunch()
    {
        var applyPoints = CleanRunCheckpoints((s, installer) => () => installer.Apply(s.Transaction));

        // Crash the transaction at a few interesting places, then crash recovery at every one of its steps.
        foreach (var crashPoint in new[] { "ready:A", "moved-out:A", "moved-in:A", "moved-out:B", "extracted:C:2", "moved-in:C" })
        {
            var n = applyPoints.IndexOf(crashPoint) + 1;
            Assert.True(n > 0, crashPoint);

            var probe = NewScenario();
            Assert.Throws<SimulatedCrashException>(() => probe.Installer(CrashAt(n)).Apply(probe.Transaction));
            var recoveryPoints = new List<string>();
            probe.Installer(recoveryPoints.Add).Recover();

            for (var m = 1; m <= recoveryPoints.Count; m++)
            {
                var s = NewScenario();
                Assert.Throws<SimulatedCrashException>(() => s.Installer(CrashAt(n)).Apply(s.Transaction));
                Assert.Throws<SimulatedCrashException>(() => s.Installer(CrashAt(m)).Recover());

                s.Installer().Recover();

                AssertConsistent(s, $"crash at {crashPoint}, then recovery crash at #{m} ({recoveryPoints[m - 1]})");
            }
        }
    }

    [Fact]
    public void CrashDuringARollbackFinishesTheRollback()
    {
        // C fails to go live, so the transaction rolls back; crash at every step of that rollback.
        Action<string> FailAtC(Action<string>? then = null) => point =>
        {
            then?.Invoke(point);
            if (point == "moved-in:C")
            {
                throw new IOException("disk said no");
            }
        };

        var points = new List<string>();
        var probe = NewScenario();
        Assert.Throws<InstallException>(() => probe.Installer(FailAtC(points.Add)).Apply(probe.Transaction));
        var rollbackStart = points.IndexOf("moved-in:C") + 1;

        for (var n = rollbackStart + 1; n <= points.Count; n++)
        {
            var s = NewScenario();
            Assert.Throws<SimulatedCrashException>(() => s.Installer(FailAtC(CrashAt(n))).Apply(s.Transaction));

            s.Installer().Recover();

            AssertConsistent(s, $"rollback crash at #{n} ({points[n - 1]})");
            // A rollback, once started, always ends with everything as it was before.
            Assert.Equal("1.0.0", s.Installer().Installed["A"].Version);
            Assert.True(s.Installer().Installed.ContainsKey("B"));
            Assert.False(s.Installer().Installed.ContainsKey("C"));
        }
    }

    [Fact]
    public void RecoveryRollsVerifiedModsForwardAndDropsUnverifiedOnes()
    {
        var points = CleanRunCheckpoints((s, installer) => () => installer.Apply(s.Transaction));
        var n = points.IndexOf("extracted:C:2") + 1; // A and B done, C half extracted

        var s = NewScenario();
        Assert.Throws<SimulatedCrashException>(() => s.Installer(CrashAt(n)).Apply(s.Transaction));
        var result = s.Installer().Recover();

        Assert.True(result.HadInterruptedTransaction);
        Assert.Equal(["A", "B"], result.RolledForward);
        Assert.Equal(["C"], result.Discarded);
        Assert.Equal("2.0.0", s.Installer().Installed["A"].Version);
        Assert.False(s.Installer().Installed.ContainsKey("B"));
        Assert.Null(s.Game.Read("Mods/GameFeatures/C"));
    }

    [Fact]
    public void NextTransactionRecoversFirstAutomatically()
    {
        var points = CleanRunCheckpoints((s, installer) => () => installer.Apply(s.Transaction));
        var s = NewScenario();
        Assert.Throws<SimulatedCrashException>(() => s.Installer(CrashAt(points.IndexOf("moved-out:A") + 1)).Apply(s.Transaction));

        // No explicit Recover(): the next Apply must clean up before doing anything else.
        s.Installer().Apply([new ModChange.Uninstall("A")]);

        Assert.False(s.Installer().Installed.ContainsKey("A"));
        Assert.Null(s.Game.Read("Mods/A"));
        Assert.Null(s.Game.Read("Mods/GameFeatures/A"));
        Assert.DoesNotContain(s.Game.WorkFolderEntries(), e => e.StartsWith("txn-") || e == "journal.json");
    }
}
