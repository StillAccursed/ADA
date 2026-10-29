using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Servers;

/// <summary>One config file that differs between a game and a server.</summary>
/// <param name="Reference">The mod, e.g. <c>SML</c>; the file is <c>Configs/{Reference}.cfg</c>.</param>
/// <param name="OnServer">False when the server has no copy yet.</param>
public sealed record ConfigDifference(string Reference, bool OnServer);

/// <summary>
/// Compares and copies mod configs (<c>FactoryGame/Configs/*.cfg</c>) from a game to a server
/// sharing its profile, for mods ADA installed on both. Never deletes a server config, and keeps
/// the server's previous files so the last push can be undone.
/// </summary>
/// <param name="isRunning">Whether an install is running; a running server reads and may rewrite its configs.</param>
public sealed class ConfigPush(ModInstallService installs, GameFilesFactory files,
    Func<GameInstall, CancellationToken, Task<bool>> isRunning, ILogger<ConfigPush>? log = null)
{
    private readonly ILogger _log = log ?? NullLogger<ConfigPush>.Instance;

    /// <summary>
    /// Configs the game has that the server lacks or has different, ignoring line endings, for mods
    /// installed on both. Reads both sides' records and files off the UI thread.
    /// </summary>
    /// <exception cref="ServerUnavailableException">The server isn't connected.</exception>
    public Task<IReadOnlyList<ConfigDifference>> CompareAsync(GameInstall game, GameInstall server, CancellationToken ct = default) =>
        Task.Run(() => Compare(game, server, ct), ct);

    /// <summary>
    /// Copies the chosen configs to the server, after backing up the server files they replace (the
    /// previous backup is dropped). Reserves and locks the server for the duration.
    /// </summary>
    /// <exception cref="GameRunningException">The server is running.</exception>
    /// <exception cref="LockHeldException">Another PC is changing the server.</exception>
    /// <exception cref="InstallationBusyException">This ADA is already changing the server.</exception>
    public async Task PushAsync(GameInstall game, GameInstall server, IReadOnlyCollection<string> references, CancellationToken ct = default)
    {
        using var operation = installs.BeginOperation(server);
        await ThrowIfRunningAsync(server, ct);
        await Task.Run(() =>
        {
            var source = files.For(game);
            var target = files.For(server);
            GameModFolders.EnsureWorkFolder(target);
            using var serverLock = target.AcquireLock(GameModFolders.LOCK);

            var chosen = references.Distinct(StringComparer.OrdinalIgnoreCase).Where(ModIdentifiers.IsValidReference).ToList();
            if (target.DirectoryExists(GameModFolders.CONFIG_BACKUP))
            {
                target.DeleteDirectory(GameModFolders.CONFIG_BACKUP);
            }
            target.CreateDirectory(GameModFolders.CONFIG_BACKUP);
            foreach (var reference in chosen.Where(r => target.FileExists(ConfigPath(r))))
            {
                var current = Read(target, ConfigPath(reference))!;
                AtomicFile.Write(target, BackupPath(reference), s => s.Write(current));
            }
            foreach (var reference in chosen)
            {
                ct.ThrowIfCancellationRequested();
                if (Read(source, ConfigPath(reference)) is { } config)
                {
                    AtomicFile.Write(target, ConfigPath(reference), s => s.Write(config));
                }
            }
            _log.LogInformation("Pushed configs for {Mods} from {Game} to {Server}", chosen, game.GamePath, server.GamePath);
        }, ct);
    }

    /// <summary>Puts back the server configs the last push replaced, then drops the backup. Same rules as <see cref="PushAsync"/>.</summary>
    /// <exception cref="GameRunningException">The server is running.</exception>
    /// <exception cref="LockHeldException">Another PC is changing the server.</exception>
    /// <exception cref="InstallationBusyException">This ADA is already changing the server.</exception>
    public async Task RestoreAsync(GameInstall server, CancellationToken ct = default)
    {
        using var operation = installs.BeginOperation(server);
        await ThrowIfRunningAsync(server, ct);
        await Task.Run(() =>
        {
            var target = files.For(server);
            if (!target.DirectoryExists(GameModFolders.CONFIG_BACKUP))
            {
                return;
            }
            using var serverLock = target.AcquireLock(GameModFolders.LOCK);
            foreach (var file in target.Files(GameModFolders.CONFIG_BACKUP).ToList())
            {
                var saved = Read(target, GamePaths.Combine(GameModFolders.CONFIG_BACKUP, file.Path))!;
                AtomicFile.Write(target, GamePaths.Combine(GameModFolders.CONFIGS, file.Path), s => s.Write(saved));
            }
            target.DeleteDirectory(GameModFolders.CONFIG_BACKUP);
            _log.LogInformation("Restored {Server}'s previous configs", server.GamePath);
        }, ct);
    }

    /// <summary>Whether the last push left a backup to restore.</summary>
    /// <exception cref="ServerUnavailableException">The server isn't connected.</exception>
    public Task<bool> HasBackupAsync(GameInstall server, CancellationToken ct = default) => Task.Run(() =>
    {
        var target = files.For(server);
        return target.DirectoryExists(GameModFolders.CONFIG_BACKUP) && target.Files(GameModFolders.CONFIG_BACKUP).Any();
    }, ct);

    private IReadOnlyList<ConfigDifference> Compare(GameInstall game, GameInstall server, CancellationToken ct)
    {
        var source = files.For(game);
        var target = files.For(server);
        var onServer = installs.InstallerFor(server).Installed;
        var shared = installs.InstallerFor(game).Installed.Keys
            .Where(onServer.ContainsKey)
            .Order(StringComparer.OrdinalIgnoreCase);

        var differences = new List<ConfigDifference>();
        foreach (var reference in shared)
        {
            ct.ThrowIfCancellationRequested();
            if (Read(source, ConfigPath(reference)) is not { } config)
            {
                continue;
            }
            var current = Read(target, ConfigPath(reference));
            if (current is null || !SameText(config, current))
            {
                differences.Add(new ConfigDifference(reference, current is not null));
            }
        }
        return differences;
    }

    private async Task ThrowIfRunningAsync(GameInstall server, CancellationToken ct)
    {
        if (await isRunning(server, ct))
        {
            throw GameRunningException.For(server);
        }
    }

    private static string ConfigPath(string reference) => $"{GameModFolders.CONFIGS}/{reference}.cfg";

    private static string BackupPath(string reference) => $"{GameModFolders.CONFIG_BACKUP}/{reference}.cfg";

    /// <returns>Null when the file isn't there.</returns>
    private static byte[]? Read(IGameFiles files, string path)
    {
        if (!files.FileExists(path))
        {
            return null;
        }
        using var stream = files.OpenRead(path);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Equal apart from line endings, which differ between a Windows game and a Linux server.</summary>
    private static bool SameText(byte[] a, byte[] b) => Normalised(a).SequenceEqual(Normalised(b));

    private static IEnumerable<byte> Normalised(byte[] bytes) => bytes.Where(b => b != (byte)'\r');
}
