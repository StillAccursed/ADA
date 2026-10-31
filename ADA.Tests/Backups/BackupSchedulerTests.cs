using ADA.Core.Backups;
using ADA.Core.Games;
using ADA.Core.Settings;
using ADA.Infrastructure.Backups;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Backups;

public sealed class BackupSchedulerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly SettingsStore _settings;
    private readonly GameInstall _game;
    private long? _freeSpace;

    public BackupSchedulerTests()
    {
        _settings = new SettingsStore(_dir.Combine("settings.json"));
        _game = new GameInstall(_dir.CreateGame("game"), GameLauncher.Epic, GameHost.CrossOver, GameKind.Client, null, "Test",
            UserDataPath: _dir.Combine("user"));
        _dir.WriteFile("user/Saved/SaveGames/acct/Atria_autosave_0.sav", "save");
        _dir.WriteFile("ada/Config/settings.json", "{}");
    }

    public void Dispose() => _dir.Dispose();

    private BackupService Service() => new(_dir.Combine("backups"), _dir.Combine("ada", "Config"), _dir.Combine("pending"),
        (_, _) => Task.FromResult(false), freeSpace: _ => _freeSpace, time: _time);

    private BackupScheduler Scheduler(BackupService service) => new(service, _settings, time: _time);

    private void Schedule(int hours, string? parts = "Saves") =>
        _settings.Update(s => s with { BackupIntervalHours = hours, BackupInstall = _game.GamePath, BackupParts = parts });

    private static async Task<IReadOnlyList<BackupInfo>> Backups(BackupService service, BackupTrigger trigger) =>
        (await service.ListAsync()).Backups.Where(b => b.Manifest.Trigger == trigger).ToList();

    [Fact]
    public async Task BacksUpAtOnceWhenThereIsNoScheduledBackupYet()
    {
        Schedule(hours: 6);
        var service = Service();
        var scheduler = Scheduler(service);

        await scheduler.CheckAsync([_game]);

        var backup = Assert.Single(await Backups(service, BackupTrigger.Scheduled));
        Assert.Equal(BackupPart.Saves, backup.Manifest.Parts);
        Assert.Equal(_time.Now + TimeSpan.FromHours(6), scheduler.NextDue);
        Assert.Null(scheduler.Problem);
    }

    [Fact]
    public async Task WaitsForTheIntervalThenCatchesUp()
    {
        Schedule(hours: 6);
        var service = Service();
        await Scheduler(service).CheckAsync([_game]);

        _time.Advance(TimeSpan.FromHours(5));
        var restarted = Scheduler(service);
        await restarted.CheckAsync([_game]);
        Assert.Single(await Backups(service, BackupTrigger.Scheduled));

        _time.Advance(TimeSpan.FromHours(2));
        await restarted.CheckAsync([_game]);
        Assert.Equal(2, (await Backups(service, BackupTrigger.Scheduled)).Count);
    }

    [Fact]
    public async Task DoesNothingWhileOff()
    {
        Schedule(hours: 0);
        var service = Service();
        var scheduler = Scheduler(service);

        await scheduler.CheckAsync([_game]);

        Assert.Empty((await service.ListAsync()).Backups);
        Assert.Null(scheduler.NextDue);
    }

    [Fact]
    public async Task SaysWhenTheChosenInstallIsGone()
    {
        Schedule(hours: 1);
        var scheduler = Scheduler(Service());

        await scheduler.CheckAsync([]);

        Assert.Equal("The install chosen for scheduled backups wasn't found. Choose another.", scheduler.Problem);
    }

    [Fact]
    public async Task AFailureWaitsBeforeTryingAgain()
    {
        Schedule(hours: 24);
        _freeSpace = 0;
        var service = Service();
        var scheduler = Scheduler(service);

        await scheduler.CheckAsync([_game]);
        Assert.StartsWith("Last scheduled backup failed: Not enough free space", scheduler.Problem);
        Assert.Equal(_time.Now + BackupScheduler.MAX_RETRY_WAIT, scheduler.NextDue);

        _freeSpace = null;
        _time.Advance(TimeSpan.FromMinutes(30));
        await scheduler.CheckAsync([_game]);
        Assert.Empty((await service.ListAsync()).Backups);

        _time.Advance(TimeSpan.FromMinutes(31));
        await scheduler.CheckAsync([_game]);
        Assert.Single((await service.ListAsync()).Backups);
        Assert.Null(scheduler.Problem);
    }

    [Fact]
    public async Task PrunesAutomaticBackupsButNeverHandMadeOnes()
    {
        _settings.Update(s => s with { KeepAutomaticBackups = 2 });
        var service = Service();
        Scheduler(service);
        await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Manual);
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            await service.CreateAsync(_game, BackupPart.Saves, BackupTrigger.Scheduled);
        }

        await Wait.Until(() => service.ListAsync().Result.Backups.Count(b => b.Manifest.Trigger == BackupTrigger.Scheduled) == 2);
        Assert.Single(await Backups(service, BackupTrigger.Manual));
    }

    [Fact]
    public async Task BacksUpSavesBeforeAModChangeOncePerCooldown()
    {
        _settings.Update(s => s with { BackupBeforeModChanges = true });
        var service = Service();
        var scheduler = Scheduler(service);

        await scheduler.BeforeModChangeAsync(_game, CancellationToken.None);
        await scheduler.BeforeModChangeAsync(_game, CancellationToken.None);
        _time.Advance(BackupScheduler.MOD_CHANGE_COOLDOWN);
        await scheduler.BeforeModChangeAsync(_game, CancellationToken.None);

        var backups = await Backups(service, BackupTrigger.BeforeModChange);
        Assert.Equal(2, backups.Count);
        Assert.All(backups, b => Assert.Equal(BackupPart.Saves, b.Manifest.Parts));
    }

    [Fact]
    public async Task NoBackupBeforeModChangesWhenTurnedOff()
    {
        var service = Service();

        await Scheduler(service).BeforeModChangeAsync(_game, CancellationToken.None);

        Assert.Empty((await service.ListAsync()).Backups);
    }

    [Fact]
    public async Task NoSavesYetDoesNotBlockAModChange()
    {
        _settings.Update(s => s with { BackupBeforeModChanges = true });
        var fresh = _game with { UserDataPath = _dir.Combine("never-launched") };

        await Scheduler(Service()).BeforeModChangeAsync(fresh, CancellationToken.None);
    }

    [Fact]
    public async Task AFailedBackupBlocksTheModChange()
    {
        _settings.Update(s => s with { BackupBeforeModChanges = true });
        _freeSpace = 0;

        var error = await Assert.ThrowsAsync<InstallException>(
            () => Scheduler(Service()).BeforeModChangeAsync(_game, CancellationToken.None));

        Assert.StartsWith("Couldn't back up your saves before changing mods, so nothing was changed", error.Message);
    }

    [Fact]
    public void ScheduledPartsAreLimitedToWhatTheInstallCanHave()
    {
        var server = new GameInstall("sftp://abc", GameLauncher.Unknown, GameHost.Linux, GameKind.DedicatedServer, null, "Box");
        var settings = AppSettings.Default with { BackupParts = "Saves, AdaConfig, Nonsense" };

        Assert.Equal(BackupPart.Saves | BackupPart.AdaConfig, BackupScheduler.PartsFor(settings, _game));
        Assert.Equal(BackupPart.AdaConfig, BackupScheduler.PartsFor(settings, server));
        Assert.Equal(BackupService.AvailableParts(_game), BackupScheduler.PartsFor(AppSettings.Default, _game));
    }
}
