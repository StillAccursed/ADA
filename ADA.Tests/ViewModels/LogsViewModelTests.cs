using ADA.Infrastructure.Logging;
using ADA.Tests.Fixtures;
using ADA.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.ViewModels;

public sealed class LogsViewModelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

    public void Dispose() => _dir.Dispose();

    private static string Line(string level, string message, int second = 0) =>
        $"2026-11-01 16:07:{second:00}.943 +00:00 [{level}] {message}";

    private void Write(string name, params string[] lines) => _dir.WriteFile(name, string.Join('\n', lines) + "\n");

    private async Task<LogsViewModel> ShowAsync()
    {
        var page = new LogsViewModel(new LogFiles(_dir.Path), NullLogger<LogsViewModel>.Instance, _time);
        await page.ShowAsync();
        return page;
    }

    [Fact]
    public async Task OpensTheNewestDayAndNamesEachDay()
    {
        Write("ada-20261031.log", Line("INF", "yesterday"));
        Write("ada-20261101.log", Line("INF", "today"));

        var page = await ShowAsync();

        Assert.Equal(["Today", "Yesterday"], page.Days.Select(d => d.Label.Split(" · ")[0]));
        Assert.Equal(["today"], page.Shown.Select(l => l.Event.Message));

        page.SelectedDay = page.Days[1];
        await page.PendingRead;

        Assert.Equal(["yesterday"], page.Shown.Select(l => l.Event.Message));
    }

    [Fact]
    public async Task FiltersByLevelAndSearch()
    {
        Write("ada-20261101.log",
            Line("INF", "Installed SML", 1),
            Line("WRN", "Couldn't delete archive", 2),
            Line("ERR", "Repair failed", 3),
            "System.IO.IOException: Disk full");
        var page = await ShowAsync();

        page.SelectedLevel = page.Levels[1];
        Assert.Equal(["Couldn't delete archive", "Repair failed"], page.Shown.Select(l => l.Event.Message));
        Assert.Equal("2 of 3 messages match", page.CountText);

        page.SelectedLevel = page.Levels[0];
        page.SearchText = "disk full";
        Assert.Equal(["Repair failed"], page.Shown.Select(l => l.Event.Message));

        page.SearchText = "nothing like this";
        Assert.Equal("No messages match these filters.", page.EmptyText);
    }

    [Fact]
    public async Task CopiesTheShownMessagesInFullAsWritten()
    {
        var longMessage = new string('x', LogLineViewModel.SHORT_LENGTH + 50);
        Write("ada-20261101.log", Line("INF", longMessage, 1), Line("ERR", "Repair failed", 2), "   at ADA.Something()");
        var page = await ShowAsync();
        string? copied = null;
        page.CopyRequested += (_, text) => copied = text;

        page.CopyCommand.Execute(null);

        Assert.Equal(string.Join('\n', Line("INF", longMessage, 1), Line("ERR", "Repair failed", 2), "   at ADA.Something()"), copied);
        Assert.Equal("Copied 2 messages.", page.Status);
        Assert.EndsWith($"… ({longMessage.Length:N0} characters)", page.Shown[0].ShortText);
    }

    [Fact]
    public async Task RefreshPicksUpNewMessagesAndKeepsTheChosenDay()
    {
        Write("ada-20261031.log", Line("INF", "yesterday"));
        Write("ada-20261101.log", Line("INF", "first"));
        var page = await ShowAsync();
        page.SelectedDay = page.Days[1];
        await page.PendingRead;

        File.AppendAllText(_dir.Combine("ada-20261031.log"), Line("INF", "later") + "\n");
        await page.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(["yesterday", "later"], page.Shown.Select(l => l.Event.Message));
    }

    [Fact]
    public async Task SaysWhenThereAreNoLogsYet()
    {
        var page = await ShowAsync();

        Assert.Equal("No log files yet.", page.EmptyText);
        Assert.False(page.CopyCommand.CanExecute(null));
    }
}
