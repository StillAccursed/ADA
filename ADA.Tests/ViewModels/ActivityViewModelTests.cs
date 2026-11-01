using System.Globalization;
using ADA.Core.Activity;
using ADA.Core.Games;
using ADA.Infrastructure.Activity;
using ADA.Infrastructure.Logging;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

public sealed class ActivityViewModelTests : IDisposable
{
    // A Sunday afternoon, in UTC, which is also the page's zone here.
    private static readonly DateTimeOffset NOW = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new(NOW, TimeZoneInfo.Utc);
    private readonly ActivityLog _log;

    public ActivityViewModelTests()
    {
        // Day names in the headers are the culture's; these tests read English ones.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        _log = new ActivityLog(_dir.Combine("activity.jsonl"), time: _time);
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>A server install, which is shown by its own name, e.g. "Atria (Dedicated Server)".</summary>
    private static GameInstall Install(string name) =>
        new($"sftp://{name}", GameLauncher.Unknown, GameHost.Linux, GameKind.DedicatedServer, name, name);

    private void RecordAt(DateTimeOffset at, string summary, ActivityArea area = ActivityArea.Mods,
        ActivityOutcome outcome = ActivityOutcome.Succeeded, GameInstall? install = null,
        IReadOnlyList<string>? details = null, string? error = null)
    {
        _time.Now = at;
        _log.Record(area, ActivityActions.MODS_CHANGED, outcome, summary, install, details, error);
        _time.Now = NOW;
    }

    private void Record(string summary, ActivityArea area = ActivityArea.Mods, ActivityOutcome outcome = ActivityOutcome.Succeeded,
        GameInstall? install = null, IReadOnlyList<string>? details = null, string? error = null) =>
        RecordAt(NOW.AddMinutes(-5), summary, area, outcome, install, details, error);

    private async Task<ActivityViewModel> ShowAsync()
    {
        // Without xUnit's context, the page applies new entries inline, as it does on the UI thread, not on a pool thread.
        SynchronizationContext.SetSynchronizationContext(null);
        var page = new ActivityViewModel(_log,
            new LogsViewModel(new LogFiles(_dir.Combine("Logs")), NullLogger<LogsViewModel>.Instance, _time),
            NullLogger<ActivityViewModel>.Instance, _time);
        await page.ShowAsync();
        return page;
    }

    private static IEnumerable<string> Shown(ActivityViewModel page) => page.Days.SelectMany(d => d.Entries.Select(e => e.Summary));

    [Fact]
    public async Task GroupsEntriesByDayNewestFirst()
    {
        RecordAt(new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero), "Thursday");
        RecordAt(new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero), "Yesterday");
        RecordAt(new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), "This morning");
        RecordAt(new(2026, 11, 1, 14, 0, 0, TimeSpan.Zero), "This afternoon");

        var page = await ShowAsync();

        Assert.Equal(["Today", "Yesterday", "Thursday 29 October"], page.Days.Select(d => d.Header));
        Assert.Equal(["This afternoon", "This morning", "Yesterday", "Thursday"], Shown(page));
        Assert.Equal("4 entries", page.CountText);
    }

    [Theory]
    [InlineData("2026-11-01", "Today")]
    [InlineData("2026-10-31", "Yesterday")]
    [InlineData("2026-10-29", "Thursday 29 October")]
    [InlineData("2025-12-31", "Wednesday 31 December 2025")]
    public void NamesDaysRelativeToToday(string day, string expected)
    {
        Assert.Equal(expected, ActivityViewModel.DayHeader(DateTime.Parse(day, CultureInfo.InvariantCulture), new DateTime(2026, 11, 1)));
    }

    [Fact]
    public async Task FiltersByAreaInstallFailuresAndSearch()
    {
        var atria = Install("Atria");
        Record("Installed 2 mods", install: atria, details: ["SML 3.12.0"]);
        Record("Backed up Saves", ActivityArea.Backups, install: atria);
        Record("Mod change failed", outcome: ActivityOutcome.Failed, install: Install("Verdana"), error: "Close Satisfactory");
        var page = await ShowAsync();

        page.SelectedArea = page.Areas.Single(a => a.Value == ActivityArea.Backups);
        Assert.Equal(["Backed up Saves"], Shown(page));
        Assert.Equal("1 of 3 entries match", page.CountText);

        page.SelectedArea = page.Areas[0];
        page.SelectedInstall = page.Installs.Single(i => i.Label == Install("Verdana").DisplayName);
        Assert.Equal(["Mod change failed"], Shown(page));

        page.SelectedInstall = page.Installs[0];
        page.FailuresOnly = true;
        Assert.Equal(["Mod change failed"], Shown(page));

        page.FailuresOnly = false;
        page.SearchText = "sml";
        Assert.Equal(["Installed 2 mods"], Shown(page));

        page.SearchText = "nothing like this";
        Assert.Empty(page.Days);
        Assert.Equal("No activity matches these filters.", page.EmptyText);
    }

    [Fact]
    public async Task OffersEachInstallInTheHistoryAndKeepsTheChoiceAsEntriesArrive()
    {
        Record("A", install: Install("Verdana"));
        Record("B", install: Install("Atria"));
        Record("C");
        var page = await ShowAsync();
        page.SelectedInstall = page.Installs.Single(i => i.Label == Install("Verdana").DisplayName);

        Record("D", install: Install("Box"));

        Assert.Equal(["All installs", .. new[] { "Atria", "Box", "Verdana" }.Select(n => Install(n).DisplayName)],
            page.Installs.Select(i => i.Label));
        Assert.Equal(Install("Verdana").DisplayName, page.SelectedInstall?.Label);
        Assert.Equal(["A"], Shown(page));
    }

    [Fact]
    public async Task InstallsWithTheSameNameAreToldApartByFolder()
    {
        Record("A", install: new GameInstall("/games/one", GameLauncher.Epic, GameHost.CrossOver, GameKind.Client, null, "Epic"));
        Record("B", install: new GameInstall("/games/two", GameLauncher.Epic, GameHost.Linux, GameKind.Client, null, "Epic"));

        var page = await ShowAsync();

        Assert.Equal(["All installs", "Satisfactory (Epic) · /games/one", "Satisfactory (Epic) · /games/two"],
            page.Installs.Select(i => i.Label));
    }

    [Fact]
    public async Task NewEntriesAppearAtTheTop()
    {
        Record("Older");
        var page = await ShowAsync();

        _time.Now = NOW;
        _log.Record(ActivityArea.Game, ActivityActions.GAME_PLAYED, ActivityOutcome.Succeeded, "Played Satisfactory for 5 m");

        Assert.Equal(["Played Satisfactory for 5 m", "Older"], Shown(page));
    }

    [Fact]
    public async Task ShowsAPageAtATime()
    {
        for (var i = 0; i < ActivityViewModel.PAGE_SIZE + 5; i++)
        {
            RecordAt(NOW.AddMinutes(-i - 1), $"Entry {i}");
        }
        var page = await ShowAsync();

        Assert.Equal(ActivityViewModel.PAGE_SIZE, Shown(page).Count());
        Assert.Equal("Show 5 more", page.ShowMoreText);

        page.ShowMoreCommand.Execute(null);

        Assert.Equal(ActivityViewModel.PAGE_SIZE + 5, Shown(page).Count());
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task OnlyEntriesWithDetailsExpand()
    {
        Record("Plain");
        Record("Failed", outcome: ActivityOutcome.Failed, error: "Disk full", details: ["Install A 1.0.0"]);
        var page = await ShowAsync();
        var failed = page.Days[0].Entries.Single(e => e.Summary == "Failed");
        var plain = page.Days[0].Entries.Single(e => e.Summary == "Plain");

        failed.ToggleCommand.Execute(null);
        plain.ToggleCommand.Execute(null);

        Assert.True(failed.IsExpanded);
        Assert.Equal(("Disk full", "⌄"), (failed.Error, failed.ExpanderGlyph));
        Assert.False(plain.IsExpanded);
        Assert.Equal("", plain.ExpanderGlyph);
    }

    [Fact]
    public async Task ClearHistoryAsksFirstThenEmptiesTheList()
    {
        Record("Installed 1 mod", install: Install("Atria"));
        var page = await ShowAsync();

        var declined = page.ClearHistoryCommand.ExecuteAsync(null);
        page.Confirmation!.CancelCommand.Execute(null);
        await declined;
        Assert.Single(Shown(page));

        var clearing = page.ClearHistoryCommand.ExecuteAsync(null);
        Assert.StartsWith("The only entry will be deleted", page.Confirmation!.Message);
        page.Confirmation.ConfirmCommand.Execute(null);
        await clearing;

        Assert.Empty(page.Days);
        Assert.Empty(await _log.ReadAsync());
        Assert.Equal(["All installs"], page.Installs.Select(i => i.Label));
        Assert.False(page.ClearHistoryCommand.CanExecute(null));
        Assert.StartsWith("Nothing recorded yet.", page.EmptyText);
    }
}
