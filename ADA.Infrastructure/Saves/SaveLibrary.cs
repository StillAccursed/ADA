using ADA.Core.Saves;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Saves;

/// <summary>
/// Lists the saves and blueprints in an install's user-data folder (<c>GameInstall.UserDataPath</c>) and
/// watches it for changes. Saves are the <c>.sav</c> files directly inside each account folder under
/// <c>Saved/SaveGames</c>. Files elsewhere, such as <c>ServerManager_V2.sav</c> in <c>SaveGames</c> itself or
/// copies in <c>SaveGames_backup</c>, aren't saves the game loads and are left out.
/// </summary>
/// <param name="readHeader">Reads a save's header, or null when it can't; <c>SaveHeaderReader.Read</c> in the app.</param>
public sealed class SaveLibrary(Func<string, SaveHeader?> readHeader, ILogger<SaveLibrary>? log = null)
{
    internal const string BLUEPRINTS_FOLDER = "blueprints";

    private const string SAVE_EXTENSION = ".sav";
    private const string BLUEPRINT_EXTENSION = ".sbp";
    private const string BLUEPRINT_CONFIG_EXTENSION = ".sbpcfg";

    private readonly ILogger _log = log ?? NullLogger<SaveLibrary>.Instance;

    public static string SaveGamesPath(string userDataPath) => Path.Combine(userDataPath, "Saved", "SaveGames");

    /// <summary>
    /// Reads the folder on a worker thread. A missing folder (the game hasn't run yet) gives empty contents;
    /// a folder that can't be listed is logged and skipped, so it doesn't hide the rest.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task<SaveFolderContents> ReadAsync(string userDataPath, CancellationToken ct = default) =>
        Task.Run(() => Read(userDataPath, ct), ct);

    /// <summary>
    /// Watches the folder's saves and blueprints until the result is disposed; see <see cref="SaveFolderWatcher"/>.
    /// </summary>
    public SaveFolderWatcher Watch(string userDataPath) => new(SaveGamesPath(userDataPath), _log);

    private SaveFolderContents Read(string userDataPath, CancellationToken ct)
    {
        var saveGames = SaveGamesPath(userDataPath);
        if (!Directory.Exists(saveGames))
        {
            return SaveFolderContents.Empty(userDataPath);
        }

        var saves = new List<SaveGame>();
        var unreadable = new List<string>();
        foreach (var account in Directories(saveGames))
        {
            var accountName = Path.GetFileName(account);
            if (accountName.Equals(BLUEPRINTS_FOLDER, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Files(account, SAVE_EXTENSION))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue; // Deleted since it was listed.
                }

                if (readHeader(file) is { } header)
                {
                    saves.Add(new SaveGame(file, accountName, info.Length, info.LastWriteTimeUtc, header));
                }
                else
                {
                    unreadable.Add(file);
                }
            }
        }

        var blueprints = new List<Blueprint>();
        foreach (var session in Directories(Path.Combine(saveGames, BLUEPRINTS_FOLDER)))
        {
            ct.ThrowIfCancellationRequested();
            blueprints.AddRange(ReadBlueprints(session));
        }

        return new SaveFolderContents(
            userDataPath,
            saves.OrderByDescending(s => s.Header.SavedAtUtc).ThenBy(s => s.FilePath, StringComparer.Ordinal).ToList(),
            unreadable.Order(StringComparer.Ordinal).ToList(),
            blueprints.OrderBy(b => b.Session, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    private IEnumerable<Blueprint> ReadBlueprints(string sessionFolder)
    {
        var session = Path.GetFileName(sessionFolder);
        foreach (var file in Files(sessionFolder, BLUEPRINT_EXTENSION))
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                continue;
            }

            var config = new FileInfo(Path.ChangeExtension(file, BLUEPRINT_CONFIG_EXTENSION));
            yield return config.Exists
                ? new Blueprint(Path.GetFileNameWithoutExtension(file), session, file, config.FullName,
                    info.Length + config.Length, Max(info.LastWriteTimeUtc, config.LastWriteTimeUtc))
                : new Blueprint(Path.GetFileNameWithoutExtension(file), session, file, null, info.Length, info.LastWriteTimeUtc);
        }
    }

    private IEnumerable<string> Directories(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetDirectories(folder) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Skipped {Folder} while listing saves", folder);
            return [];
        }
    }

    /// <summary>Files whose extension is exactly <paramref name="extension"/>, ignoring case.</summary>
    private IEnumerable<string> Files(string folder, string extension)
    {
        try
        {
            return Directory.GetFiles(folder)
                .Where(f => Path.GetExtension(f).Equals(extension, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Skipped {Folder} while listing saves", folder);
            return [];
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
