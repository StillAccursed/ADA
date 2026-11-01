using System.IO.Compression;
using System.Text;
using ADA.Core.Activity;
using ADA.Core.Backups;
using ADA.Core.Games;
using ADA.Infrastructure.Activity;
using ADA.Infrastructure.Backups;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Backups;

public sealed class BackupServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GameInstall _game;
    private bool _gameRunning;
    private long? _freeSpace;

    public BackupServiceTests()
    {
        _game = new GameInstall(_dir.CreateGame("game"), GameLauncher.Epic, GameHost.CrossOver, GameKind.Client, null, "Test",
            UserDataPath: _dir.Combine("user"));
        Write("user/Saved/SaveGames/acct/Atria_autosave_0.sav", "save 0");
        Write("user/Saved/SaveGames/ServerManager_V2.sav", "servers");
        Write("user/Saved/SaveGames/blueprints/Atria/Factory.sbp", "blueprint");
        Write("user/Saved/Config/Windows/Engine.ini", "engine");
        Write("user/Saved/Config/CrashReportClient/UECC/CrashReportClient.ini", "crash");
        Write("game/FactoryGame/Configs/AutoRun.cfg", "autorun");
        Write("ada/Config/settings.json", "{}");
        Write("ada/Config/Profiles/Atria.olp", "profile");
        Write("user/Saved/SaveGames/acct/write.tmp", "temp");
    }

    public void Dispose() => _dir.Dispose();

    private string PathOf(string relative) => _dir.Combine(relative.Split('/'));

    private void Write(string relative, string content) => _dir.WriteFile(relative, content);

    private string Read(string relative) => File.ReadAllText(PathOf(relative));

    private BackupService Service(ActivityLog? activity = null) => new(_dir.Combine("backups"), _dir.Combine("ada", "Config"),
        _dir.Combine("pending"), (_, _) => Task.FromResult(_gameRunning), freeSpace: _ => _freeSpace, activity: activity);

    private ActivityLog Activity() => new(_dir.Combine("activity.jsonl"));

    private static IReadOnlyList<string> Entries(BackupInfo backup)
    {
        using var zip = ZipFile.OpenRead(backup.FilePath);
        return zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task BacksUpEveryPartIntoItsOwnFolderInTheZip()
    {
        var backup = await Service().CreateAsync(_game, BackupPart.All, BackupTrigger.Manual);

        Assert.Equal(
        [
            "AdaConfig/Profiles/Atria.olp", "AdaConfig/settings.json", "Blueprints/Atria/Factory.sbp",
            "GameConfig/Windows/Engine.ini", "ModConfig/AutoRun.cfg", "Saves/ServerManager_V2.sav",
            "Saves/acct/Atria_autosave_0.sav", "manifest.json",
        ], Entries(backup));
        Assert.Equal(BackupPart.All, backup.Manifest.Parts);
        Assert.Equal(BackupTrigger.Manual, backup.Manifest.Trigger);
        Assert.Equal(_game.UserDataPath, backup.Manifest.Source.UserDataPath);
        Assert.Equal(7, backup.Manifest.Files.Count);
        Assert.All(backup.Manifest.Files, f => Assert.Equal(64, f.Sha256.Length));
        Assert.Empty(Directory.GetFiles(_dir.Combine("backups"), "*.partial"));
    }

    [Fact]
    public async Task BacksUpOnlyTheChosenParts()
    {
        var backup = await Service().CreateAsync(_game, BackupPart.ModConfig | BackupPart.GameConfig, BackupTrigger.Manual);

        Assert.Equal(["GameConfig/Windows/Engine.ini", "ModConfig/AutoRun.cfg", "manifest.json"], Entries(backup));
    }

    [Fact]
    public async Task RefusesPartsTheInstallDoesNotHave()
    {
        var server = new GameInstall("sftp://abc", GameLauncher.Unknown, GameHost.Linux, GameKind.DedicatedServer, null, "Box");

        Assert.Equal(BackupPart.AdaConfig, BackupService.AvailableParts(server));
        await Assert.ThrowsAsync<BackupException>(() => Service().CreateAsync(server, BackupPart.Saves, BackupTrigger.Manual));
        await Assert.ThrowsAsync<BackupException>(() => Service().CreateAsync(_game, BackupPart.None, BackupTrigger.Manual));
    }

    /// <summary>An install whose game has never run, so its user-data folder doesn't exist yet.</summary>
    private GameInstall NeverLaunched() => new(_dir.CreateGame("fresh"), GameLauncher.Steam, GameHost.CrossOver, GameKind.Client,
        null, "Fresh", UserDataPath: _dir.Combine("never-launched"));

    [Fact]
    public async Task RefusesABackupWithNothingInIt()
    {
        var error = await Assert.ThrowsAsync<NothingToBackUpException>(
            () => Service().CreateAsync(NeverLaunched(), BackupPart.Saves | BackupPart.GameConfig, BackupTrigger.Manual));

        Assert.StartsWith("There's nothing to back up yet", error.Message);
        Assert.False(Directory.Exists(_dir.Combine("backups")));
    }

    [Fact]
    public async Task RestoreIntoANeverLaunchedInstallNeedsNoSafetyCopy()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Manual);

        var result = await service.RestoreAsync(backup, NeverLaunched(), BackupPart.Saves);

        Assert.Null(result.SafetyBackup);
        Assert.Equal("save 0", File.ReadAllText(_dir.Combine("never-launched", "Saved", "SaveGames", "acct", "Atria_autosave_0.sav")));
        Assert.Single((await service.ListAsync()).Backups);
    }

    [Fact]
    public async Task RefusesWhenThereIsNotEnoughSpaceAndLeavesNothing()
    {
        _freeSpace = 10;

        await Assert.ThrowsAsync<IOException>(() => Service().CreateAsync(_game, BackupPart.All, BackupTrigger.Manual));

        Assert.Empty(Directory.GetFiles(_dir.Combine("backups")));
    }

    [Fact]
    public async Task CancellingLeavesNoBackupBehind()
    {
        var progress = new CancelOnFirstReport();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service().CreateAsync(_game, BackupPart.All, BackupTrigger.Manual, progress, progress.Token));

        Assert.Empty(Directory.GetFiles(_dir.Combine("backups")));
    }

    [Fact]
    public async Task ListsNewestFirstAndReportsUnreadableFiles()
    {
        var service = Service();
        var first = await service.CreateAsync(_game, BackupPart.AdaConfig, BackupTrigger.Manual);
        var second = await service.CreateAsync(_game, BackupPart.AdaConfig, BackupTrigger.Scheduled);
        Write("backups/not a backup.zip", "junk");
        Write("backups/half.zip.partial", "junk");

        var listing = await service.ListAsync();

        Assert.Equal([second.FilePath, first.FilePath], listing.Backups.Select(b => b.FilePath));
        Assert.Equal([PathOf("backups/not a backup.zip")], listing.Unreadable);
    }

    [Fact]
    public async Task RestoreBringsFilesBackAndKeepsNewerOnes()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.Saves | BackupPart.ModConfig, BackupTrigger.Manual);
        Write("user/Saved/SaveGames/acct/Atria_autosave_0.sav", "broken");
        Write("user/Saved/SaveGames/acct/Atria_autosave_1.sav", "newer");
        File.Delete(PathOf("game/FactoryGame/Configs/AutoRun.cfg"));

        var result = await service.RestoreAsync(backup, _game, BackupPart.Saves | BackupPart.ModConfig);

        Assert.Equal("save 0", Read("user/Saved/SaveGames/acct/Atria_autosave_0.sav"));
        Assert.Equal("newer", Read("user/Saved/SaveGames/acct/Atria_autosave_1.sav"));
        Assert.Equal("autorun", Read("game/FactoryGame/Configs/AutoRun.cfg"));
        Assert.Equal(3, result.FilesRestored);
        Assert.False(result.AdaConfigPending);
    }

    [Fact]
    public async Task RestoreTakesASafetyBackupOfWhatItReplaces()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Manual);
        Write("user/Saved/SaveGames/acct/Atria_autosave_0.sav", "current");

        var result = await service.RestoreAsync(backup, _game, BackupPart.Saves);

        Assert.NotNull(result.SafetyBackup);
        Assert.Equal(BackupTrigger.BeforeRestore, result.SafetyBackup.Manifest.Trigger);
        using var zip = ZipFile.OpenRead(result.SafetyBackup.FilePath);
        using var reader = new StreamReader(zip.GetEntry("Saves/acct/Atria_autosave_0.sav")!.Open());
        Assert.Equal("current", reader.ReadToEnd());
    }

    [Fact]
    public async Task RestoreOnlyTouchesTheChosenParts()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.All, BackupTrigger.Manual);
        Write("user/Saved/Config/Windows/Engine.ini", "changed");
        Write("game/FactoryGame/Configs/AutoRun.cfg", "changed");

        await service.RestoreAsync(backup, _game, BackupPart.ModConfig);

        Assert.Equal("autorun", Read("game/FactoryGame/Configs/AutoRun.cfg"));
        Assert.Equal("changed", Read("user/Saved/Config/Windows/Engine.ini"));
    }

    [Fact]
    public async Task RestoreRefusesWhileTheGameRuns()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Manual);
        Write("user/Saved/SaveGames/acct/Atria_autosave_0.sav", "current");
        _gameRunning = true;

        await Assert.ThrowsAsync<BackupException>(() => service.RestoreAsync(backup, _game, BackupPart.Saves));

        Assert.Equal("current", Read("user/Saved/SaveGames/acct/Atria_autosave_0.sav"));
        Assert.Single((await service.ListAsync()).Backups);
    }

    [Fact]
    public async Task RestoreRefusesADamagedBackupBeforeTouchingAnything()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Manual);
        using (var zip = ZipFile.Open(backup.FilePath, ZipArchiveMode.Update))
        {
            zip.GetEntry("Saves/acct/Atria_autosave_0.sav")!.Delete();
            using var stream = zip.CreateEntry("Saves/acct/Atria_autosave_0.sav").Open();
            stream.Write("tampered"u8);
        }
        Write("user/Saved/SaveGames/acct/Atria_autosave_0.sav", "current");

        var error = await Assert.ThrowsAsync<BackupException>(() => service.RestoreAsync(backup, _game, BackupPart.Saves));

        Assert.StartsWith("This backup is damaged", error.Message);
        Assert.Equal("current", Read("user/Saved/SaveGames/acct/Atria_autosave_0.sav"));
        Assert.Single((await service.ListAsync()).Backups);
    }

    [Fact]
    public async Task RestoreRefusesPathsThatLeaveThePart()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Manual);
        var evil = backup with
        {
            Manifest = backup.Manifest with
            {
                Files = [.. backup.Manifest.Files, new BackupFile("Saves/../../evil.txt", 1, new string('0', 64))],
            },
        };

        await Assert.ThrowsAsync<BackupException>(() => service.RestoreAsync(evil, _game, BackupPart.Saves));

        Assert.False(File.Exists(PathOf("user/Saved/evil.txt")));
    }

    [Fact]
    public async Task RestoreRefusesPartsTheBackupDoesNotHold()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Manual);

        await Assert.ThrowsAsync<BackupException>(() => service.RestoreAsync(backup, _game, BackupPart.ModConfig));
    }

    [Fact]
    public async Task AdaConfigIsStagedAndAppliedAtTheNextStart()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.AdaConfig, BackupTrigger.Manual);
        Write("ada/Config/settings.json", "changed");
        Write("ada/Config/servers.json", "kept");

        var result = await service.RestoreAsync(backup, _game, BackupPart.AdaConfig);

        Assert.True(result.AdaConfigPending);
        Assert.Equal("changed", Read("ada/Config/settings.json"));

        Assert.Equal(2, PendingAdaConfig.Apply(_dir.Combine("pending"), _dir.Combine("ada", "Config")));
        Assert.Equal("{}", Read("ada/Config/settings.json"));
        Assert.Equal("kept", Read("ada/Config/servers.json"));
        Assert.False(Directory.Exists(_dir.Combine("pending")));
        Assert.Equal(0, PendingAdaConfig.Apply(_dir.Combine("pending"), _dir.Combine("ada", "Config")));
    }

    [Fact]
    public async Task RemovesLeftoverPartialFilesBeforeTheNextBackup()
    {
        Write("backups/ADA 2026-01-01 000000 Manual.zip.partial", "crashed");

        await Service().CreateAsync(_game, BackupPart.AdaConfig, BackupTrigger.Manual);

        Assert.Empty(Directory.GetFiles(_dir.Combine("backups"), "*.partial"));
    }

    [Fact]
    public async Task DeleteRemovesTheBackup()
    {
        var service = Service();
        var backup = await service.CreateAsync(_game, BackupPart.AdaConfig, BackupTrigger.Manual);

        await service.DeleteAsync(backup);

        Assert.Empty((await service.ListAsync()).Backups);
    }

    [Fact]
    public async Task PruneKeepsTheNewestOfOneKindFromOneInstall()
    {
        var service = Service();
        var other = _game with { UserDataPath = _dir.Combine("user") + "-other" };
        Write("user-other/Saved/SaveGames/acct/x.sav", "other");
        var oldest = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Scheduled);
        var middle = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Scheduled);
        var newest = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Scheduled);
        var otherInstall = await service.CreateAsync(other, BackupPart.Saves, BackupTrigger.Scheduled);
        var manual = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Manual);

        Assert.Equal(1, await service.PruneAsync(BackupTrigger.Scheduled, newest.Manifest.Source, keep: 2));

        Assert.Equal([manual.FilePath, otherInstall.FilePath, newest.FilePath, middle.FilePath],
            (await service.ListAsync()).Backups.Select(b => b.FilePath));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PruneAsync(BackupTrigger.Manual, manual.Manifest.Source, 1));
    }

    [Fact]
    public async Task RaisesBackupCreatedForSafetyCopiesToo()
    {
        var service = Service();
        var created = new List<BackupTrigger>();
        service.BackupCreated += (_, b) => created.Add(b.Manifest.Trigger);
        var backup = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Manual);

        await service.RestoreAsync(backup, _game, BackupPart.Saves);

        Assert.Equal([BackupTrigger.Manual, BackupTrigger.BeforeRestore], created);
    }

    [Fact]
    public async Task RecordsHandMadeBackupsButNotSuccessfulScheduledOnes()
    {
        var activity = Activity();
        var service = Service(activity);

        var manual = await service.CreateAsync(_game, BackupPart.Saves | BackupPart.ModConfig, BackupTrigger.Manual);
        await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Scheduled);

        var entry = Assert.Single(await activity.ReadAsync());
        Assert.Equal((ActivityActions.BACKUP_CREATED, ActivityOutcome.Succeeded), (entry.Action, entry.Outcome));
        Assert.StartsWith("Backed up Saves, Mod settings (", entry.Summary);
        Assert.Equal([Path.GetFileName(manual.FilePath)], entry.Details);
    }

    [Fact]
    public async Task RecordsAFailedScheduledBackup()
    {
        var activity = Activity();
        _freeSpace = 10;

        await Assert.ThrowsAsync<IOException>(() => Service(activity).CreateAsync(_game, BackupPart.All, BackupTrigger.Scheduled));

        var entry = Assert.Single(await activity.ReadAsync());
        Assert.Equal((ActivityOutcome.Failed, "Scheduled backup failed"), (entry.Outcome, entry.Summary));
        Assert.NotNull(entry.Error);
    }

    [Fact]
    public async Task NothingToBackUpIsOnlyAFailureWhenDoneByHand()
    {
        var activity = Activity();
        var service = Service(activity);

        foreach (var trigger in new[] { BackupTrigger.Scheduled, BackupTrigger.BeforeModChange, BackupTrigger.Manual })
        {
            await Assert.ThrowsAsync<NothingToBackUpException>(() => service.CreateAsync(NeverLaunched(), BackupPart.Saves, trigger));
        }

        var entry = Assert.Single(await activity.ReadAsync());
        Assert.Equal((ActivityOutcome.Failed, "Backup failed"), (entry.Outcome, entry.Summary));
        Assert.StartsWith("There's nothing to back up yet", entry.Error);
    }

    [Fact]
    public async Task RecordsRestoresAndDeletesButNotPruning()
    {
        var activity = Activity();
        var service = Service(activity);
        var first = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Scheduled);
        var second = await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Scheduled);
        await service.PruneAsync(BackupTrigger.Scheduled, second.Manifest.Source, keep: 1);

        await service.RestoreAsync(second, _game, BackupPart.Saves);
        var safety = (await service.ListAsync()).Backups.Single(b => b.Manifest.Trigger == BackupTrigger.BeforeRestore);
        await service.DeleteAsync(second);

        var entries = await activity.ReadAsync();
        Assert.Equal([ActivityActions.BACKUP_DELETED, ActivityActions.BACKUP_RESTORED], entries.Select(e => e.Action));
        Assert.StartsWith("Deleted the backup from ", entries[0].Summary);
        Assert.Equal("Restored Saves (2 files)", entries[1].Summary);
        Assert.Equal([$"From {Path.GetFileName(second.FilePath)}", $"Safety copy: {Path.GetFileName(safety.FilePath)}"],
            entries[1].Details);
        Assert.False(File.Exists(first.FilePath));
    }

    [Fact]
    public async Task ReadsTheFolderEachTime()
    {
        var folder = _dir.Combine("first");
        var service = new BackupService(() => folder, _dir.Combine("ada", "Config"), _dir.Combine("pending"),
            (_, _) => Task.FromResult(false));
        await service.CreateAsync(_game, BackupPart.AdaConfig, BackupTrigger.Manual);

        folder = _dir.Combine("second");

        Assert.Empty((await service.ListAsync()).Backups);
        Assert.Equal(folder, service.Folder);
    }

    /// <summary>Cancels the backup as soon as it reports its first file.</summary>
    private sealed class CancelOnFirstReport : IProgress<BackupProgress>
    {
        private readonly CancellationTokenSource _cts = new();

        public CancellationToken Token => _cts.Token;

        public void Report(BackupProgress value) => _cts.Cancel();
    }
}
