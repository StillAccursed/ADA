using ADA.Infrastructure.Saves;
using ADA.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Saves;

public class SaveFolderWatcherTests
{
    private static readonly TimeSpan SETTLE = TimeSpan.FromMilliseconds(100);

    private static SaveFolderWatcher Watch(string folder) => new(folder, NullLogger.Instance, SETTLE);

    [Fact]
    public async Task BurstOfChangesRaisesChangedOnce()
    {
        using var dir = new TempDir();
        using var watcher = Watch(dir.Path);
        var raised = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref raised);

        for (var i = 0; i < 10; i++)
        {
            watcher.NoteChange();
        }
        await Wait.Until(() => Volatile.Read(ref raised) > 0);
        await Task.Delay(SETTLE * 3);

        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task NothingIsRaisedAfterDispose()
    {
        using var dir = new TempDir();
        var watcher = Watch(dir.Path);
        var raised = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref raised);

        watcher.NoteChange();
        watcher.Dispose();
        await Task.Delay(SETTLE * 3);
        watcher.NoteChange();

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public void MissingFolderIsNotWatched()
    {
        using var dir = new TempDir();
        using var watcher = Watch(dir.Combine("missing"));

        Assert.False(watcher.IsWatching);
    }

    [Fact]
    public async Task NewSaveInAccountFolderRaisesChanged()
    {
        using var dir = new TempDir();
        var account = Directory.CreateDirectory(dir.Combine("139504cc558c414395144c448756526e")).FullName;
        using var watcher = Watch(dir.Path);
        var raised = 0;
        watcher.Changed += (_, _) => Interlocked.Increment(ref raised);

        File.WriteAllBytes(Path.Combine(account, "Atria_autosave_0.sav"), new SaveHeaderBuilder().Build());

        Assert.True(watcher.IsWatching);
        await Wait.Until(() => Volatile.Read(ref raised) > 0);
    }
}
