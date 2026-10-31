using System.Collections.Concurrent;
using ADA.Core.Backups;
using ADA.Core.Games;
using ADA.Core.Settings;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Backups;

/// <summary>
/// Runs scheduled backups while ADA is open, catching up at launch; prunes automatic backups after each one; and
/// backs up saves before mod changes when that's turned on. Its settings are read when used, so changes take effect
/// at the next check. <see cref="StatusChanged"/> is raised on whichever thread changed the status.
/// </summary>
public sealed class BackupScheduler
{
    internal static readonly TimeSpan CHECK_INTERVAL = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan START_DELAY = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MOD_CHANGE_COOLDOWN = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan MAX_RETRY_WAIT = TimeSpan.FromHours(1);

    private readonly BackupService _backups;
    private readonly SettingsStore _settings;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastBeforeModChange = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastScheduled;
    private string? _lastScheduledFor;
    private DateTimeOffset? _waitUntil;

    /// <summary>The status changed: <see cref="NextDue"/> or <see cref="Problem"/>.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>When the next scheduled backup is due; null when the schedule is off or can't run.</summary>
    public DateTimeOffset? NextDue { get; private set; }

    /// <summary>Why the last scheduled backup failed or can't run; null when all is well.</summary>
    public string? Problem { get; private set; }

    public BackupScheduler(BackupService backups, SettingsStore settings, ILogger<BackupScheduler>? log = null, TimeProvider? time = null)
    {
        (_backups, _settings, _time) = (backups, settings, time ?? TimeProvider.System);
        _log = log ?? NullLogger<BackupScheduler>.Instance;
        backups.BackupCreated += OnBackupCreated;
    }

    /// <summary>
    /// Checks every <see cref="CHECK_INTERVAL"/> until <paramref name="ct"/> is cancelled, after waiting
    /// <see cref="START_DELAY"/>. Never throws, except to end on cancellation.
    /// </summary>
    /// <param name="installs">The detected installs, read at each check; called on the thread that started this loop.</param>
    public async Task RunAsync(Func<IReadOnlyList<GameInstall>> installs, CancellationToken ct = default)
    {
        await Task.Delay(START_DELAY, _time, ct);
        using var timer = new PeriodicTimer(CHECK_INTERVAL, _time);
        do
        {
            await CheckAsync(installs(), ct);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>One check: backs up the chosen install if a scheduled backup is due. Never throws, except on cancellation.</summary>
    internal async Task CheckAsync(IReadOnlyList<GameInstall> installs, CancellationToken ct = default)
    {
        var settings = _settings.Current;
        if (settings.BackupIntervalHours <= 0 || settings.BackupInstall is null)
        {
            SetStatus(null, settings.BackupIntervalHours <= 0 ? null : "Choose an install for scheduled backups.");
            return;
        }
        if (installs.FirstOrDefault(i => i.GamePath == settings.BackupInstall && !i.IsRemote) is not { } game)
        {
            SetStatus(null, "The install chosen for scheduled backups wasn't found. Choose another.");
            return;
        }

        var interval = TimeSpan.FromHours(settings.BackupIntervalHours);
        var last = await LastScheduledAsync(game, ct);
        var now = _time.GetUtcNow();
        var due = last is null ? now : last.Value + interval;
        if (_waitUntil is { } wait && wait > due)
        {
            due = wait;
        }
        if (now < due)
        {
            SetStatus(due, Problem);
            return;
        }

        try
        {
            var parts = PartsFor(settings, game);
            var backup = await _backups.CreateAsync(game, parts, BackupTrigger.Scheduled, ct: ct);
            _lastScheduled = new DateTimeOffset(backup.Manifest.CreatedUtc, TimeSpan.Zero);
            _waitUntil = null;
            SetStatus(_lastScheduled + interval, null);
        }
        catch (NothingToBackUpException)
        {
            _waitUntil = now + interval;
            SetStatus(_waitUntil, "Nothing to back up yet.");
        }
        catch (Exception e) when (e is BackupException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Scheduled backup of {Game} failed", game.DisplayName);
            _waitUntil = now + (interval < MAX_RETRY_WAIT ? interval : MAX_RETRY_WAIT);
            SetStatus(_waitUntil, $"Last scheduled backup failed: {e.Message}");
        }
    }

    /// <summary>
    /// The hook <see cref="ModInstallService"/> calls before changing a local install's mods: backs up Saves when
    /// "back up before mod changes" is on, at most once per <see cref="MOD_CHANGE_COOLDOWN"/> per install. No saves
    /// yet never blocks the change.
    /// </summary>
    /// <exception cref="InstallException">The backup failed, so the change must not go ahead.</exception>
    public async Task BeforeModChangeAsync(GameInstall game, CancellationToken ct)
    {
        if (!_settings.Current.BackupBeforeModChanges || game.IsRemote || game.UserDataPath is null)
        {
            return;
        }
        var now = _time.GetUtcNow();
        if (_lastBeforeModChange.TryGetValue(game.GamePath, out var last) && now - last < MOD_CHANGE_COOLDOWN)
        {
            return;
        }

        try
        {
            await _backups.CreateAsync(game, BackupPart.Saves, BackupTrigger.BeforeModChange, ct: ct);
            _lastBeforeModChange[game.GamePath] = now;
        }
        catch (NothingToBackUpException)
        {
            _log.LogInformation("No saves to back up before changing mods in {Game}", game.DisplayName);
        }
        catch (Exception e) when (e is BackupException or IOException or UnauthorizedAccessException)
        {
            throw new InstallException(
                $"Couldn't back up your saves before changing mods, so nothing was changed: {e.Message} "
                + "You can turn this off on the Backups page.", e);
        }
    }

    /// <summary>The parts the schedule backs up for <paramref name="game"/>: the saved choice, limited to what it can have.</summary>
    public static BackupPart PartsFor(AppSettings settings, GameInstall game)
    {
        var chosen = settings.BackupParts is null
            ? BackupPart.All
            : settings.BackupParts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(name => Enum.TryParse<BackupPart>(name, out var part) ? part : BackupPart.None)
                .Aggregate(BackupPart.None, (all, part) => all | part);
        return chosen & BackupService.AvailableParts(game);
    }

    private async Task<DateTimeOffset?> LastScheduledAsync(GameInstall game, CancellationToken ct)
    {
        if (_lastScheduledFor != game.GamePath)
        {
            var source = new BackupSource(game.DisplayName, game.GamePath, game.UserDataPath);
            var listing = await _backups.ListAsync(ct);
            _lastScheduled = listing.Backups
                .Where(b => b.Manifest.Trigger == BackupTrigger.Scheduled && BackupService.IsSameInstall(b.Manifest.Source, source))
                .Select(b => (DateTimeOffset?)new DateTimeOffset(b.Manifest.CreatedUtc, TimeSpan.Zero))
                .FirstOrDefault();
            _lastScheduledFor = game.GamePath;
        }
        return _lastScheduled;
    }

    // Runs while the backup service holds its lock, so pruning goes on in the background.
    private void OnBackupCreated(object? sender, BackupInfo backup)
    {
        if (backup.Manifest.Trigger == BackupTrigger.Manual)
        {
            return;
        }
        var keep = _settings.Current.KeepAutomaticBackups;
        _ = Task.Run(async () =>
        {
            try
            {
                var deleted = await _backups.PruneAsync(backup.Manifest.Trigger, backup.Manifest.Source, keep);
                if (deleted > 0)
                {
                    _log.LogInformation("Pruned {Count} old {Trigger} backups of {Game}", deleted, backup.Manifest.Trigger,
                        backup.Manifest.Source.DisplayName);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(e, "Pruning old backups failed");
            }
        });
    }

    private void SetStatus(DateTimeOffset? nextDue, string? problem)
    {
        if (NextDue == nextDue && Problem == problem)
        {
            return;
        }
        (NextDue, Problem) = (nextDue, problem);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
