using ADA.Core.Backups;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Backups;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Settings;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

public sealed class BackupsViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _game;
    private readonly SettingsStore _settings;
    private bool _gameRunning;

    public BackupsViewModelTests()
    {
        _game = _dir.CreateGame("game");
        _settings = new SettingsStore(_dir.Combine("settings.json"));
        _dir.WriteFile("user/Saved/SaveGames/acct/Atria_autosave_0.sav", "save");
        _dir.WriteFile("user/Saved/Config/Windows/Engine.ini", "engine");
        _dir.WriteFile("game/FactoryGame/Configs/AutoRun.cfg", "autorun");
        _dir.WriteFile("ada/Config/settings.json", "{}");
    }

    public void Dispose() => _dir.Dispose();

    private string SavePath => _dir.Combine("user", "Saved", "SaveGames", "acct", "Atria_autosave_0.sav");

    private GameInstall Install => new(_game, GameLauncher.Epic, GameHost.CrossOver, GameKind.Client, null, "Test",
        UserDataPath: _dir.Combine("user"));

    /// <summary>Reads its folder from settings, as the app's does.</summary>
    private BackupService Service => new(() => _settings.Current.BackupFolder ?? _dir.Combine("backups"),
        _dir.Combine("ada", "Config"), _dir.Combine("pending"), (_, _) => Task.FromResult(_gameRunning));

    private async Task<BackupsViewModel> OpenAsync()
    {
        var settings = _settings;
        var versions = new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([]));
        var installs = new ModInstallService(settings, new(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
            versions, (_, _) => Task.FromResult(false));
        var profiles = new GameProfiles(new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json")), installs);
        var dashboard = DashboardFactory.Create(_dir, new KnownGames([Install]),
            g => new GameInstallViewModel(g, installs, new ModResolutionService(versions, installs), profiles,
                NullLogger<GameInstallViewModel>.Instance),
            settings: settings, installs: installs, profiles: profiles);
        await dashboard.ScanCommand.ExecuteAsync(null);

        var service = Service;
        var schedule = new BackupScheduleViewModel(settings, new BackupScheduler(service, settings), dashboard, _dir.Combine("backups"),
            NullLogger.Instance);
        var page = new BackupsViewModel(service, dashboard, schedule, NullLogger<BackupsViewModel>.Instance);
        await page.ShowAsync();
        return page;
    }

    private static void Untick(BackupsViewModel page, BackupPart part) => page.Parts.Single(p => p.Part == part).IsChecked = false;

    [Fact]
    public async Task OffersEveryPartTickedForALocalInstall()
    {
        var page = await OpenAsync();

        Assert.Equal(BackupService.Split(BackupPart.All), page.Parts.Select(p => p.Part));
        Assert.All(page.Parts, p => Assert.True(p.IsAvailable && p.IsChecked));
        Assert.Equal("Satisfactory (Epic)", page.InstallText);
    }

    [Fact]
    public void PartsAnInstallCannotHaveAreGreyedOutWithAReason()
    {
        var options = PartOptionViewModel.For(BackupPart.All, BackupPart.AdaConfig | BackupPart.ModConfig);

        var saves = options.Single(o => o.Part == BackupPart.Saves);
        Assert.False(saves.IsAvailable);
        Assert.False(saves.IsChecked);
        Assert.Equal("ADA doesn't know where this install keeps its saves and settings.", saves.UnavailableReason);
        Assert.Equal(BackupPart.AdaConfig | BackupPart.ModConfig, PartOptionViewModel.Chosen(options));
    }

    [Fact]
    public async Task BacksUpTheTickedPartsAndListsTheBackup()
    {
        var page = await OpenAsync();
        Untick(page, BackupPart.ModConfig);

        await page.BackUpNowCommand.ExecuteAsync(null);

        var item = Assert.Single(page.Backups);
        Assert.Equal(BackupPart.All & ~BackupPart.ModConfig, item.Backup.Manifest.Parts);
        Assert.Equal("Made by hand", item.TriggerText);
        Assert.StartsWith("Backed up Saves, Blueprints, Game settings, ADA settings (", page.Notice);
        Assert.False(page.IsBusy);
    }

    [Fact]
    public async Task CannotBackUpWithNothingTicked()
    {
        var page = await OpenAsync();

        foreach (var part in page.Parts)
        {
            part.IsChecked = false;
        }

        Assert.False(page.BackUpNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeletingAsksFirst()
    {
        await Service.CreateAsync(Install, BackupPart.AdaConfig, BackupTrigger.Manual);
        var page = await OpenAsync();
        var item = Assert.Single(page.Backups);

        var deleting = page.DeleteCommand.ExecuteAsync(item);
        await Wait.Until(() => page.Confirmation is not null);
        Assert.True(page.Confirmation!.IsDanger);
        page.Confirmation.CancelCommand.Execute(null);
        await deleting;
        Assert.Single(page.Backups);

        deleting = page.DeleteCommand.ExecuteAsync(item);
        await Wait.Until(() => page.Confirmation is not null);
        page.Confirmation!.ConfirmCommand.Execute(null);
        await deleting;
        Assert.Empty(page.Backups);
    }

    [Fact]
    public async Task RestoreOffersTheBackupsPartsWithAdaSettingsUnticked()
    {
        await Service.CreateAsync(Install, BackupPart.Saves | BackupPart.AdaConfig, BackupTrigger.Manual);
        var page = await OpenAsync();

        page.RestoreCommand.Execute(page.Backups[0]);

        Assert.True(page.IsRestoring);
        Assert.Equal([(BackupPart.Saves, true), (BackupPart.AdaConfig, false)],
            page.Restoring!.Parts.Select(p => (p.Part, p.IsChecked)));
        Assert.Null(page.Restoring.DifferentSourceText);
    }

    [Fact]
    public async Task RestoreBringsFilesBackAndSaysWhereTheReplacedOnesAre()
    {
        await Service.CreateAsync(Install, BackupPart.Saves, BackupTrigger.Manual);
        File.WriteAllText(SavePath, "broken");
        var page = await OpenAsync();
        page.RestoreCommand.Execute(page.Backups[0]);

        await page.Restoring!.RestoreCommand.ExecuteAsync(null);

        Assert.Equal("save", File.ReadAllText(SavePath));
        Assert.True(page.Restoring.IsDone);
        Assert.Contains("BeforeRestore", page.Restoring.Result);
        Assert.False(page.Restoring.NeedsRestart);

        page.Restoring.CloseCommand.Execute(null);
        await page.PendingRefresh;
        Assert.False(page.IsRestoring);
        Assert.Equal(2, page.Backups.Count);
    }

    [Fact]
    public async Task RestoreShowsWhyItWasRefused()
    {
        await Service.CreateAsync(Install, BackupPart.Saves, BackupTrigger.Manual);
        var page = await OpenAsync();
        page.RestoreCommand.Execute(page.Backups[0]);
        _gameRunning = true;

        await page.Restoring!.RestoreCommand.ExecuteAsync(null);

        Assert.StartsWith("Close Satisfactory before restoring", page.Restoring.Problem);
        Assert.False(page.Restoring.IsDone);
    }

    [Fact]
    public async Task RestoringAdaSettingsOffersARestart()
    {
        await Service.CreateAsync(Install, BackupPart.AdaConfig, BackupTrigger.Manual);
        var page = await OpenAsync();
        var restartRequested = false;
        page.RestartRequested += (_, _) => restartRequested = true;
        page.RestoreCommand.Execute(page.Backups[0]);
        page.Restoring!.Parts.Single().IsChecked = true;

        await page.Restoring.RestoreCommand.ExecuteAsync(null);
        page.Restoring.RestartNowCommand.Execute(null);

        Assert.True(page.Restoring.NeedsRestart);
        Assert.EndsWith("ADA's settings will be restored when ADA restarts.", page.Restoring.Result);
        Assert.True(restartRequested);
    }

    [Fact]
    public async Task WarnsWhenTheBackupIsFromAnotherInstall()
    {
        _dir.WriteFile("other/Saved/SaveGames/acct/Verdana.sav", "other");
        var other = Install with { UserDataPath = _dir.Combine("other"), Launcher = GameLauncher.Steam };
        await Service.CreateAsync(other, BackupPart.Saves, BackupTrigger.Manual);
        var page = await OpenAsync();

        page.RestoreCommand.Execute(page.Backups[0]);

        Assert.Equal("This backup was taken from Satisfactory (Steam). Restoring writes its files into Satisfactory (Epic) instead.",
            page.Restoring!.DifferentSourceText);
    }

    [Fact]
    public async Task ReportsFilesThatAreNotReadableBackups()
    {
        _dir.WriteFile("backups/junk.zip", "junk");

        var page = await OpenAsync();

        Assert.Equal("1 file in the backup folder isn't a readable backup.", page.UnreadableText);
    }

    [Fact]
    public async Task TurningTheScheduleOnPicksTheSidebarInstallAndSaves()
    {
        var page = await OpenAsync();

        page.Schedule.Interval = page.Schedule.Intervals.Single(i => i.Value == 6);

        Assert.Equal(_game, page.Schedule.Install?.Value);
        Assert.Equal("The first scheduled backup runs within a minute.", page.Schedule.StatusText);
        await Wait.Until(() => _settings.Current is { BackupIntervalHours: 6 } s && s.BackupInstall == _game);
    }

    [Fact]
    public async Task ChangingTheFolderListsTheNewFolder()
    {
        await Service.CreateAsync(Install, BackupPart.AdaConfig, BackupTrigger.Manual);
        var page = await OpenAsync();
        Assert.Single(page.Backups);

        page.Schedule.ChangeFolder(_dir.Combine("elsewhere"));
        await Wait.Until(() => _settings.Current.BackupFolder == _dir.Combine("elsewhere"));
        await page.PendingRefresh;

        Assert.Empty(page.Backups);
        Assert.Equal("Backups made before stay in the old folder.", page.Schedule.FolderNotice);
    }

    [Theory]
    [InlineData(30, "Next backup in under a minute.")]
    [InlineData(300, "Next backup in 5 m.")]
    [InlineData(8100, "Next backup in 2 h 15 m.")]
    public void NextBackupText(int seconds, string expected)
    {
        Assert.Equal(expected, BackupScheduleViewModel.NextText(TimeSpan.FromSeconds(seconds)));
    }

    private sealed class KnownGames(GameInstall[] installs) : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => installs;
    }
}
