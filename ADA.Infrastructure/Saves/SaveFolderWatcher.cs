using Microsoft.Extensions.Logging;

namespace ADA.Infrastructure.Saves;

/// <summary>
/// Watches one <c>SaveGames</c> folder, including blueprints, and raises <see cref="Changed"/> once a burst
/// of changes has been quiet for the settle time: the game writes an autosave over a second or two. Watches
/// nothing when the folder doesn't exist yet; the game creates it on first run, and the page re-reads
/// whenever it's shown.
/// </summary>
public sealed class SaveFolderWatcher : IDisposable
{
    internal static readonly TimeSpan DEFAULT_SETTLE_TIME = TimeSpan.FromSeconds(1);

    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _settle;
    private readonly TimeSpan _settleTime;
    private readonly ILogger _log;
    private int _disposed;

    /// <summary>
    /// Raised on a worker thread after changes settle; never after <see cref="Dispose"/> returns.
    /// Handlers re-read with <see cref="SaveLibrary.ReadAsync"/>.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>False when the folder didn't exist or couldn't be watched; <see cref="Changed"/> then never fires.</summary>
    public bool IsWatching => _watcher is not null;

    /// <param name="settleTime">Quiet time before <see cref="Changed"/> fires; <see cref="DEFAULT_SETTLE_TIME"/> by default.</param>
    internal SaveFolderWatcher(string saveGamesPath, ILogger log, TimeSpan? settleTime = null)
    {
        _log = log;
        _settleTime = settleTime ?? DEFAULT_SETTLE_TIME;
        _settle = new Timer(_ => RaiseChanged());
        if (!Directory.Exists(saveGamesPath))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(saveGamesPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Created += OnFileEvent;
            _watcher.Changed += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _log.LogWarning(e, "Can't watch {Folder} for save changes", saveGamesPath);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _watcher?.Dispose();
        _settle.Dispose();
    }

    /// <summary>Restarts the settle timer, so a burst of changes raises <see cref="Changed"/> once. Ignored after disposal.</summary>
    internal void NoteChange()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        try
        {
            _settle.Change(_settleTime, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the restart.
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => NoteChange();

    // An overflowed buffer drops events; a re-read catches up with whatever was missed.
    private void OnError(object sender, ErrorEventArgs e)
    {
        _log.LogWarning(e.GetException(), "Save folder watcher lost events; re-reading");
        NoteChange();
    }

    private void RaiseChanged()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
