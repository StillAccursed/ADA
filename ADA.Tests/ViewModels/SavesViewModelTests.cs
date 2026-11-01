using ADA.Core.Activity;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Profiles;
using ADA.Infrastructure.Activity;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using ADA.Infrastructure.Saves;
using ADA.Infrastructure.Settings;
using ADA.Platform.Formats;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

public sealed class SavesViewModelTests : IDisposable
{
    private const string ACCOUNT = "139504cc558c414395144c448756526e";

    private const string THREE_MODS = """
        {"Version":1,"Mods":[
          {"Reference":"LongerBeams","Name":"Longer Beams","Version":"1.0.2"},
          {"Reference":"Achievements","Name":"Achievements","Version":"2.0.7"},
          {"Reference":"ExtraSwatchSlots","Name":"64 Extra Swatch Slots","Version":"1.0.2"}
        ]}
        """;

    private readonly TempDir _dir = new();
    private readonly SettingsStore _settings;
    private readonly ProfileStore _profileStore;
    private readonly List<SavesViewModel> _pages = [];
    private DashboardViewModel? _dashboard;

    public SavesViewModelTests()
    {
        _settings = new SettingsStore(_dir.Combine("settings.json"));
        _profileStore = new ProfileStore(_dir.Combine("Profiles"), _dir.Combine("profiles.json"));
    }

    /// <summary>Hides every page first, so their folder watchers stop before the temp folder is deleted.</summary>
    public void Dispose()
    {
        foreach (var page in _pages)
        {
            page.Hide();
        }
        _dir.Dispose();
    }

    private string UserData => _dir.Combine("user");

    private string SavePath(string name) => _dir.Combine("user", "Saved", "SaveGames", ACCOUNT, $"{name}.sav");

    private void WriteSave(string name, string session = "Atria", int playSeconds = 600, int hour = 12, string? mods = null)
    {
        var path = SavePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var header = new SaveHeaderBuilder().SaveName(name).Session(session).PlaySeconds(playSeconds)
            .SavedAtTicks(new DateTime(2026, 10, 25, hour, 0, 0, DateTimeKind.Utc).Ticks);
        if (mods is not null)
        {
            header.ModMetadata(mods);
        }
        File.WriteAllBytes(path, header.Build());
    }

    private string BlueprintPath(string session, string name) =>
        _dir.Combine("user", "Saved", "SaveGames", "blueprints", session, $"{name}.sbp");

    private void WriteBlueprint(string session, string name, string content = "sbp")
    {
        var path = BlueprintPath(session, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <param name="knowsSaveFolder">False gives the install no <c>UserDataPath</c>, as for Legendary without Heroic.</param>
    private async Task<SavesViewModel> ShowAsync(bool knowsSaveFolder = true, ActivityLog? activity = null)
    {
        var game = _dir.CreateGame("game");
        var install = new GameInstall(game, GameLauncher.Epic, GameHost.CrossOver, GameKind.Client, null, "Test",
            UserDataPath: knowsSaveFolder ? UserData : null);
        var versions = new FakeVersionSource((_, _) => Task.FromResult<IReadOnlyList<CatalogVersion>>([]));
        var installs = new ModInstallService(_settings, new(new HttpClient(new FakeHttpHandler()), _dir.Combine("Archives")),
            versions, (_, _) => Task.FromResult(false));
        var profiles = new GameProfiles(_profileStore, installs);
        _dashboard = DashboardFactory.Create(_dir, new KnownGames([install]),
            g => new GameInstallViewModel(g, installs, new ModResolutionService(versions, installs), profiles,
                NullLogger<GameInstallViewModel>.Instance),
            settings: _settings, installs: installs, profiles: profiles);
        await _dashboard.ScanCommand.ExecuteAsync(null);
        await Wait.Until(() => _dashboard.SelectedInstall is { IsBusy: false });

        var page = new SavesViewModel(_dashboard, new SaveLibrary(SaveHeaderReader.Read), new BlueprintTransfer(), installs, profiles, _settings,
            NullLogger<SavesViewModel>.Instance, activity);
        _pages.Add(page);
        await page.ShowAsync();
        return page;
    }

    /// <summary>Opens the only session's newest save.</summary>
    private static void OpenFirstSave(SavesViewModel page)
    {
        page.SessionSelection = page.Shown[0];
        page.SaveSelection = page.Session!.Saves[0];
    }

    [Fact]
    public async Task ListsSessionsNewestFirstIncludingBlueprintOnlyOnes()
    {
        WriteSave("Atria_autosave_0", session: "Atria", hour: 9, playSeconds: 300);
        WriteSave("Atria_autosave_1", session: "Atria", hour: 12, playSeconds: 600);
        WriteSave("Verdana_autosave_0", session: "Verdana", hour: 15);
        WriteBlueprint("Empty", "Wall");
        var page = await ShowAsync();

        Assert.Equal(["Verdana", "Atria", "Empty"], page.Shown.Select(s => s.Name));
        var atria = page.Shown.Single(s => s.Name == "Atria");
        Assert.Equal("2 saves", atria.SavesText);
        Assert.Equal("10 m", atria.PlayTimeText);
        Assert.Equal("1 blueprint", page.Shown.Single(s => s.Name == "Empty").BlueprintsText);
        Assert.Equal("3 sessions · 3 saves", page.ResultsText);
    }

    [Fact]
    public async Task SearchMatchesSessionAndSaveNames()
    {
        WriteSave("Atria_autosave_0", session: "Atria");
        WriteSave("Before the train", session: "Verdana");
        var page = await ShowAsync();

        page.SearchText = "train";
        Assert.Equal(["Verdana"], page.Shown.Select(s => s.Name));
        Assert.Equal("1 of 2 sessions", page.ResultsText);

        page.SearchText = "atr";
        Assert.Equal(["Atria"], page.Shown.Select(s => s.Name));
    }

    [Fact]
    public async Task SortsSessionsByNameAndPlaytime()
    {
        WriteSave("b", session: "B", playSeconds: 60, hour: 15);
        WriteSave("a", session: "A", playSeconds: 30, hour: 9);
        WriteSave("c", session: "C", playSeconds: 90, hour: 12);
        var page = await ShowAsync();

        page.Sort = page.Sorts.Single(o => o.Value == SaveSort.Name);
        Assert.Equal(["A", "B", "C"], page.Shown.Select(s => s.Name));

        page.Sort = page.Sorts.Single(o => o.Value == SaveSort.PlayTime);
        Assert.Equal(["C", "B", "A"], page.Shown.Select(s => s.Name));
    }

    [Fact]
    public async Task OpensSessionThenSaveAndGoesBackALevelAtATime()
    {
        WriteSave("Atria_autosave_0", hour: 9);
        WriteSave("Atria_autosave_1", hour: 12);
        var page = await ShowAsync();

        page.SessionSelection = page.Shown[0];
        Assert.True(page.ShowsSession);
        Assert.Null(page.SessionSelection);
        Assert.Equal(["Atria_autosave_1", "Atria_autosave_0"], page.Session!.Saves.Select(s => s.Name));

        page.SaveSelection = page.Session.Saves[1];
        Assert.True(page.ShowsSave);
        Assert.Null(page.SaveSelection);
        Assert.Equal("Atria_autosave_0", page.Save!.Name);

        page.BackCommand.Execute(null);
        Assert.True(page.ShowsSession);
        page.BackCommand.Execute(null);
        Assert.True(page.ShowsSessionList);
    }

    [Fact]
    public async Task BreadcrumbJumpsStraightToTheList()
    {
        WriteSave("Atria_autosave_0");
        var page = await ShowAsync();
        OpenFirstSave(page);

        page.ShowSessionsCommand.Execute(null);

        Assert.True(page.ShowsSessionList);
        Assert.Null(page.Session);
        Assert.Null(page.Save);
    }

    [Fact]
    public async Task SessionPageShowsItsBlueprintsAndSaysTheyAreShared()
    {
        WriteSave("Atria_autosave_0");
        WriteSave("Atria_autosave_1");
        WriteBlueprint("Atria", "Factory");
        var page = await ShowAsync();

        page.SessionSelection = page.Shown[0];

        Assert.Equal("Factory", Assert.Single(page.Session!.Blueprints).Name);
        Assert.Equal("Blueprints belong to the session, so all 2 saves share them.", page.Session.BlueprintsNote);
    }

    [Fact]
    public async Task RefreshKeepsTheOpenSessionAndSave()
    {
        WriteSave("Atria_autosave_0");
        var page = await ShowAsync();
        OpenFirstSave(page);
        page.Save!.ModSearch = "x";

        WriteSave("Atria_autosave_1", hour: 13);
        await page.RefreshAsync();

        Assert.Equal(2, page.Session!.Saves.Count);
        Assert.Equal("Atria_autosave_0", page.Save?.Name);
        Assert.Equal("x", page.Save?.ModSearch);
    }

    [Fact]
    public async Task RefreshGoesBackToTheSessionWhenTheOpenSaveIsDeleted()
    {
        WriteSave("Atria_autosave_0");
        WriteSave("Atria_autosave_1");
        var page = await ShowAsync();
        page.SessionSelection = page.Shown[0];
        page.SaveSelection = page.Session!.Saves.Single(s => s.Name == "Atria_autosave_0");

        File.Delete(SavePath("Atria_autosave_0"));
        await page.RefreshAsync();

        Assert.True(page.ShowsSession);
        Assert.Equal("\"Atria_autosave_0\" is no longer in the save folder.", page.Notice);
    }

    [Fact]
    public async Task RefreshClosesASessionThatIsGone()
    {
        WriteSave("Atria_autosave_0");
        WriteSave("Verdana_autosave_0", session: "Verdana");
        var page = await ShowAsync();
        page.SessionSelection = page.Shown.Single(s => s.Name == "Atria");

        File.Delete(SavePath("Atria_autosave_0"));
        await page.RefreshAsync();

        Assert.True(page.ShowsSessionList);
        Assert.Equal("Session \"Atria\" no longer has any saves or blueprints.", page.Notice);
    }

    [Fact]
    public async Task InstallMissingModsRequestsTheMissingReferences()
    {
        WriteSave("Atria_autosave_0", mods: THREE_MODS);
        var page = await ShowAsync();
        IReadOnlyList<string>? requested = null;
        page.InstallRequested += (_, references) => requested = references;
        OpenFirstSave(page);

        page.InstallMissingModsCommand.Execute(null);

        Assert.Equal(["Achievements", "ExtraSwatchSlots", "LongerBeams"], requested!.Order());
    }

    [Fact]
    public async Task SwitchToSaveModsOpensTheSwitchDialogWithAFreeProfileName()
    {
        WriteSave("Atria_autosave_0", mods: THREE_MODS);
        _profileStore.Save(new ModProfile("Atria", []));
        var page = await ShowAsync();
        OpenFirstSave(page);

        await page.SwitchToSaveModsCommand.ExecuteAsync(null);

        Assert.Contains("\"Atria (2)\"", _dashboard!.SwitchPrompt?.Title);
        Assert.Null(page.ActionNotice);
    }

    [Fact]
    public async Task CancellingTheSwitchLeavesNoProfileBehind()
    {
        WriteSave("Atria_autosave_0", mods: THREE_MODS);
        var page = await ShowAsync();
        OpenFirstSave(page);
        await page.SwitchToSaveModsCommand.ExecuteAsync(null);
        Assert.Contains("\"Atria\"", _dashboard!.SwitchPrompt?.Title);

        _dashboard.SwitchPrompt!.CancelCommand.Execute(null);

        Assert.Null(_dashboard.SwitchPrompt);
        Assert.Null(_profileStore.Get("Atria"));
    }

    [Fact]
    public async Task RemembersTheLayout()
    {
        var page = await ShowAsync();

        page.IsCardView = true;

        await Wait.Until(() => _settings.Current.SavesView == "Card");
    }

    [Fact]
    public async Task ExplainsAnInstallWithNoSavesYet()
    {
        var page = await ShowAsync();

        Assert.True(page.ShowsEmptyState);
        Assert.StartsWith("No saves yet", page.EmptyText);
    }

    [Fact]
    public async Task ExplainsAnInstallWithNoKnownSaveFolder()
    {
        var page = await ShowAsync(knowsSaveFolder: false);

        Assert.Equal("ADA can't tell where this install keeps its saves.", page.EmptyText);
    }

    [Fact]
    public void ExplainsServersAndNoInstall()
    {
        var server = new GameInstall("sftp://abc", GameLauncher.Unknown, GameHost.Linux, GameKind.DedicatedServer, null, "Box");

        Assert.StartsWith("Saves on dedicated servers", SavesViewModel.WhyNoSaves(server));
        Assert.Equal("No Satisfactory install found.", SavesViewModel.WhyNoSaves(null));
    }

    [Fact]
    public async Task ReportsUnreadableSaves()
    {
        WriteSave("Good");
        File.WriteAllBytes(SavePath("Broken"), [1, 2, 3]);
        var page = await ShowAsync();

        Assert.Single(page.Shown);
        Assert.StartsWith("1 save couldn't be read", page.Notice);
    }

    private static void OpenSession(SavesViewModel page, string name) =>
        page.SessionSelection = page.Shown.Single(s => s.Name == name);

    [Fact]
    public async Task CopiesTickedBlueprintsToAnotherSession()
    {
        WriteSave("Atria_autosave_0");
        WriteSave("Verdana_autosave_0", session: "Verdana");
        WriteBlueprint("Atria", "Factory");
        WriteBlueprint("Atria", "Belt");
        var page = await ShowAsync();
        OpenSession(page, "Atria");
        Assert.Equal(["Verdana"], page.Session!.CopyTargets);

        page.Session.Blueprints.Single(b => b.Name == "Factory").IsSelected = true;
        await page.CopyBlueprintsCommand.ExecuteAsync("Verdana");

        Assert.StartsWith("Copied 1 blueprint to Verdana.", page.BlueprintNotice);
        Assert.Equal("1 blueprint", page.Shown.Single(s => s.Name == "Verdana").BlueprintsText);
    }

    [Fact]
    public async Task RecordsCopiedBlueprints()
    {
        var activity = new ActivityLog(_dir.Combine("activity.jsonl"));
        WriteSave("Atria_autosave_0");
        WriteSave("Verdana_autosave_0", session: "Verdana");
        WriteBlueprint("Atria", "Factory");
        var page = await ShowAsync(activity: activity);
        OpenSession(page, "Atria");

        page.Session!.Blueprints.Single().IsSelected = true;
        await page.CopyBlueprintsCommand.ExecuteAsync("Verdana");

        var entry = Assert.Single(await activity.ReadAsync());
        Assert.Equal((ActivityArea.Saves, ActivityActions.BLUEPRINTS_COPIED, "Copied 1 blueprint to Verdana"),
            (entry.Area, entry.Action, entry.Summary));
        Assert.Equal(["Factory"], entry.Details);
    }

    [Fact]
    public async Task AsksOnceAboutNamesTheTargetHasAndCanReplaceThem()
    {
        WriteSave("Atria_autosave_0");
        WriteSave("Verdana_autosave_0", session: "Verdana");
        WriteBlueprint("Atria", "Factory", content: "new");
        WriteBlueprint("Verdana", "Factory", content: "old");
        var page = await ShowAsync();
        OpenSession(page, "Atria");
        page.Session!.SelectAllCommand.Execute(null);

        var copying = page.CopyBlueprintsCommand.ExecuteAsync("Verdana");
        await Wait.Until(() => page.Confirmation is not null);
        Assert.Equal("Verdana already has Factory.", page.Confirmation!.Message);
        page.Confirmation.OptionChecked = true;
        page.Confirmation.ConfirmCommand.Execute(null);
        await copying;

        Assert.Equal("new", File.ReadAllText(BlueprintPath("Verdana", "Factory")));
    }

    [Fact]
    public async Task CancellingTheQuestionWritesNothing()
    {
        WriteSave("Atria_autosave_0");
        WriteSave("Verdana_autosave_0", session: "Verdana");
        WriteBlueprint("Atria", "Factory", content: "new");
        WriteBlueprint("Verdana", "Factory", content: "old");
        var page = await ShowAsync();
        OpenSession(page, "Atria");
        page.Session!.SelectAllCommand.Execute(null);

        var copying = page.CopyBlueprintsCommand.ExecuteAsync("Verdana");
        await Wait.Until(() => page.Confirmation is not null);
        page.Confirmation!.CancelCommand.Execute(null);
        await copying;

        Assert.Equal("old", File.ReadAllText(BlueprintPath("Verdana", "Factory")));
        Assert.False(File.Exists(BlueprintPath("Verdana", "Factory (2)")));
    }

    [Fact]
    public async Task BlueprintWritesWaitForTheGameToClose()
    {
        WriteSave("Atria_autosave_0");
        WriteSave("Verdana_autosave_0", session: "Verdana");
        WriteBlueprint("Atria", "Factory");
        var page = await ShowAsync();
        OpenSession(page, "Atria");
        page.Session!.SelectAllCommand.Execute(null);
        _dashboard!.SelectedInstall!.IsGameRunning = true;

        await page.CopyBlueprintsCommand.ExecuteAsync("Verdana");

        Assert.StartsWith("Close Satisfactory first", page.BlueprintNotice);
        Assert.False(File.Exists(BlueprintPath("Verdana", "Factory")));
    }

    [Fact]
    public async Task ExportsAllWhenNoneIsTickedAndImportsIntoTheOpenSession()
    {
        WriteSave("Atria_autosave_0");
        WriteSave("Verdana_autosave_0", session: "Verdana");
        WriteBlueprint("Atria", "Factory");
        WriteBlueprint("Atria", "Belt");
        var page = await ShowAsync();
        OpenSession(page, "Atria");
        var zip = _dir.Combine("atria.zip");
        await using (var stream = File.Create(zip))
        {
            await page.ExportBlueprintsAsync(stream);
        }
        Assert.Equal("Exported 2 blueprints.", page.BlueprintNotice);

        page.ShowSessionsCommand.Execute(null);
        OpenSession(page, "Verdana");
        await page.ImportBlueprintsAsync([zip]);

        Assert.StartsWith("Imported 2 blueprints into Verdana.", page.BlueprintNotice);
        Assert.Equal(2, page.Session!.Blueprints.Count);
    }

    [Fact]
    public async Task TicksSurviveARefresh()
    {
        WriteSave("Atria_autosave_0");
        WriteBlueprint("Atria", "Factory");
        WriteBlueprint("Atria", "Belt");
        var page = await ShowAsync();
        OpenSession(page, "Atria");
        page.Session!.Blueprints.Single(b => b.Name == "Belt").IsSelected = true;

        await page.RefreshAsync();

        Assert.Equal(["Belt"], page.Session!.SelectedBlueprints.Select(b => b.Name));
    }

    private sealed class KnownGames(GameInstall[] installs) : IGameDetector
    {
        public IReadOnlyList<GameInstall> Detect() => installs;
    }
}
