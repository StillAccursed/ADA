using System.IO.Compression;
using ADA.Core.Saves;
using ADA.Infrastructure.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Infrastructure.Saves;

/// <summary>What to do with an incoming blueprint whose name the target session already has.</summary>
public enum BlueprintClash
{
    /// <summary>Overwrite the existing blueprint.</summary>
    Replace,

    /// <summary>Keep the existing blueprint; the incoming one gets the first free "Name (2)", "Name (3)"….</summary>
    KeepBoth,
}

/// <summary>A blueprint's two files, read into memory to copy, export or import.</summary>
/// <param name="Config">The <c>.sbpcfg</c>, or null when the blueprint has none.</param>
public sealed record BlueprintFiles(string Name, byte[] Blueprint, byte[]? Config);

/// <summary>Blueprints read from files the user chose, and what was left out and why.</summary>
public sealed record BlueprintImport(IReadOnlyList<BlueprintFiles> Blueprints, IReadOnlyList<string> Problems);

/// <summary>What a copy or import wrote.</summary>
/// <param name="Written">The names written, after any renaming.</param>
/// <param name="Renamed">Incoming names that clashed and were kept under a new name, as (incoming, written).</param>
public sealed record BlueprintWriteResult(IReadOnlyList<string> Written, IReadOnlyList<(string From, string To)> Renamed);

/// <summary>
/// Copies blueprints between sessions, and exports and imports them as <c>.zip</c> files. A blueprint is a
/// <c>.sbp</c> and, usually, a <c>.sbpcfg</c> of the same name in <c>SaveGames/blueprints/&lt;session&gt;</c>;
/// the game names a blueprint after its files. Both files of a pair are written together with
/// <see cref="AtomicFile.WriteAll"/>, so a failure never leaves half a blueprint. The game's
/// <c>BlueprintManager_Data.json</c> is never touched.
/// </summary>
/// <param name="maxFileBytes">The largest blueprint file accepted; real ones are kilobytes to a few MB.</param>
public sealed class BlueprintTransfer(ILogger<BlueprintTransfer>? log = null, long maxFileBytes = BlueprintTransfer.DEFAULT_MAX_FILE_BYTES)
{
    internal const long DEFAULT_MAX_FILE_BYTES = 64L * 1024 * 1024;

    private const string BLUEPRINT_EXTENSION = ".sbp";
    private const string CONFIG_EXTENSION = ".sbpcfg";
    private const string ZIP_EXTENSION = ".zip";
    private const int MAX_COPY_NUMBER = 999;
    private const int MAX_NAME_LENGTH = 200;

    // Characters Windows forbids in file names; blueprints must work in the Windows game whatever OS ADA runs on.
    private static readonly char[] UNSAFE_NAME_CHARS = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly ILogger _log = log ?? NullLogger<BlueprintTransfer>.Instance;

    /// <summary>The folder holding <paramref name="session"/>'s blueprints, whether or not it exists yet.</summary>
    /// <exception cref="ArgumentException">The session name can't be a folder name.</exception>
    public static string SessionFolder(string userDataPath, string session)
    {
        if (!IsSafeName(session))
        {
            throw new ArgumentException($"\"{session}\" can't be used as a blueprint folder name.", nameof(session));
        }
        return Path.Combine(SaveLibrary.SaveGamesPath(userDataPath), SaveLibrary.BLUEPRINTS_FOLDER, session);
    }

    /// <returns>Which of <paramref name="names"/> the session already has, ignoring case.</returns>
    /// <exception cref="ArgumentException">The session name can't be a folder name.</exception>
    public IReadOnlyList<string> Clashes(string userDataPath, string session, IEnumerable<string> names)
    {
        var existing = ExistingNames(SessionFolder(userDataPath, session));
        return names.Where(existing.ContainsKey).ToList();
    }

    /// <summary>Reads listed blueprints' files, for copying or exporting.</summary>
    /// <exception cref="IOException">A file can't be read.</exception>
    /// <exception cref="InvalidDataException">A file is larger than the limit.</exception>
    public Task<IReadOnlyList<BlueprintFiles>> ReadAsync(IReadOnlyList<Blueprint> blueprints, CancellationToken ct = default) =>
        Task.Run(() => (IReadOnlyList<BlueprintFiles>)blueprints.Select(b =>
        {
            ct.ThrowIfCancellationRequested();
            return new BlueprintFiles(b.Name, ReadFile(b.BlueprintPath), b.ConfigPath is null ? null : ReadFile(b.ConfigPath));
        }).ToList(), ct);

    /// <summary>
    /// Writes blueprints into <paramref name="session"/>'s folder, creating it if needed. With
    /// <see cref="BlueprintClash.Replace"/>, a clash is written under the existing file's casing, and the old
    /// <c>.sbpcfg</c> is removed when the incoming blueprint has none.
    /// </summary>
    /// <exception cref="ArgumentException">The session name can't be a folder name.</exception>
    /// <exception cref="IOException">Writing failed; pairs written before it stay written.</exception>
    public Task<BlueprintWriteResult> WriteAsync(string userDataPath, string session, IReadOnlyList<BlueprintFiles> blueprints,
        BlueprintClash clash, CancellationToken ct = default) => Task.Run(() =>
    {
        var folder = SessionFolder(userDataPath, session);
        Directory.CreateDirectory(folder);
        var existing = ExistingNames(folder);
        var written = new List<string>();
        var renamed = new List<(string From, string To)>();
        foreach (var blueprint in blueprints)
        {
            ct.ThrowIfCancellationRequested();
            var name = blueprint.Name;
            if (existing.TryGetValue(name, out var existingName))
            {
                if (clash == BlueprintClash.Replace)
                {
                    name = existingName;
                }
                else
                {
                    name = FreeName(name, existing);
                    renamed.Add((blueprint.Name, name));
                }
            }

            var target = Path.Combine(folder, name);
            var files = new List<(string Path, Action<Stream> Write)> { (target + BLUEPRINT_EXTENSION, s => s.Write(blueprint.Blueprint)) };
            if (blueprint.Config is { } config)
            {
                files.Add((target + CONFIG_EXTENSION, s => s.Write(config)));
            }
            AtomicFile.WriteAll(files);
            if (blueprint.Config is null)
            {
                File.Delete(target + CONFIG_EXTENSION);
            }
            existing[name] = name;
            written.Add(name);
        }
        _log.LogInformation("Wrote {Count} blueprints to {Folder} ({Renamed} renamed)", written.Count, folder, renamed.Count);
        return new BlueprintWriteResult(written, renamed);
    }, ct);

    /// <summary>Writes blueprints to <paramref name="zip"/> as a <c>.zip</c> with their files at its root, as blueprint sites share them.</summary>
    public Task ExportAsync(IReadOnlyList<BlueprintFiles> blueprints, Stream zip, CancellationToken ct = default) => Task.Run(() =>
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var blueprint in blueprints)
        {
            ct.ThrowIfCancellationRequested();
            AddEntry(archive, blueprint.Name + BLUEPRINT_EXTENSION, blueprint.Blueprint);
            if (blueprint.Config is { } config)
            {
                AddEntry(archive, blueprint.Name + CONFIG_EXTENSION, config);
            }
        }
    }, ct);

    /// <summary>
    /// Reads blueprints from files the user chose: <c>.zip</c> files, and <c>.sbp</c> files with any <c>.sbpcfg</c>
    /// beside them. Inside a zip only <c>.sbp</c>/<c>.sbpcfg</c> entries are read, each by its file name alone,
    /// so nothing can be written outside the session folder. Unsafe names, files over the limit, a
    /// <c>.sbpcfg</c> without its <c>.sbp</c>, and repeated names (the first is kept) are left out and listed.
    /// </summary>
    public Task<BlueprintImport> ReadImportAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => Task.Run(() =>
    {
        var blueprints = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var configs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path);
            try
            {
                if (extension.Equals(ZIP_EXTENSION, StringComparison.OrdinalIgnoreCase))
                {
                    using var archive = ZipFile.OpenRead(path);
                    foreach (var entry in archive.Entries)
                    {
                        var fileName = entry.FullName[(entry.FullName.LastIndexOfAny(['/', '\\']) + 1)..];
                        Collect(fileName, entry.Length, () => ReadLimited(entry.Open(), fileName), blueprints, configs, problems);
                    }
                }
                else if (extension.Equals(BLUEPRINT_EXTENSION, StringComparison.OrdinalIgnoreCase))
                {
                    Collect(Path.GetFileName(path), new FileInfo(path).Length, () => ReadFile(path), blueprints, configs, problems);
                    var config = Path.ChangeExtension(path, CONFIG_EXTENSION);
                    if (File.Exists(config))
                    {
                        Collect(Path.GetFileName(config), new FileInfo(config).Length, () => ReadFile(config), blueprints, configs, problems);
                    }
                }
                else
                {
                    problems.Add($"{Path.GetFileName(path)} isn't a blueprint or a .zip, so it was left out.");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                problems.Add($"{Path.GetFileName(path)} couldn't be read: {e.Message}");
            }
        }

        foreach (var orphan in configs.Keys.Where(name => !blueprints.ContainsKey(name)).Order(StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{orphan}{CONFIG_EXTENSION} has no {BLUEPRINT_EXTENSION} with it, so it was left out.");
        }
        var read = blueprints
            .OrderBy(b => b.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(b => new BlueprintFiles(b.Key, b.Value, configs.GetValueOrDefault(b.Key)))
            .ToList();
        return new BlueprintImport(read, problems);
    }, ct);

    /// <summary>
    /// Whether <paramref name="name"/> works as a file or folder name in the Windows game: not empty, no
    /// forbidden or control characters, no trailing dot or space, and not <c>.</c> or <c>..</c>.
    /// </summary>
    internal static bool IsSafeName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MAX_NAME_LENGTH
        && name is not ("." or "..")
        && name.IndexOfAny(UNSAFE_NAME_CHARS) < 0
        && !name.Any(char.IsControl)
        && !name.EndsWith('.') && !name.EndsWith(' ');

    /// <summary>Files and zip entries that aren't blueprint files are ignored without a problem.</summary>
    private void Collect(string fileName, long declaredLength, Func<byte[]> read, Dictionary<string, byte[]> blueprints,
        Dictionary<string, byte[]> configs, List<string> problems)
    {
        var extension = Path.GetExtension(fileName);
        var into = extension.Equals(BLUEPRINT_EXTENSION, StringComparison.OrdinalIgnoreCase) ? blueprints
            : extension.Equals(CONFIG_EXTENSION, StringComparison.OrdinalIgnoreCase) ? configs
            : null;
        if (into is null)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        if (!IsSafeName(name))
        {
            problems.Add($"{fileName} has a name the game can't use, so it was left out.");
            return;
        }
        if (into.ContainsKey(name))
        {
            problems.Add($"{fileName} was chosen more than once; the first was kept.");
            return;
        }
        if (declaredLength > maxFileBytes)
        {
            problems.Add($"{fileName} is larger than {maxFileBytes / (1024 * 1024)} MB, so it was left out.");
            return;
        }

        try
        {
            into[name] = read();
        }
        catch (InvalidDataException)
        {
            problems.Add($"{fileName} is larger than {maxFileBytes / (1024 * 1024)} MB, so it was left out.");
        }
    }

    /// <exception cref="InvalidDataException">The file is larger than the limit.</exception>
    private byte[] ReadFile(string path) => ReadLimited(File.OpenRead(path), Path.GetFileName(path));

    /// <summary>Reads at most the limit, whatever a size field claims; disposes <paramref name="source"/>.</summary>
    /// <exception cref="InvalidDataException">There's more than the limit.</exception>
    private byte[] ReadLimited(Stream source, string fileName)
    {
        using (source)
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[81920];
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > maxFileBytes)
                {
                    throw new InvalidDataException($"{fileName} is larger than the limit.");
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    /// <returns>Blueprint names in <paramref name="folder"/>, ignoring case, mapped to their actual casing.</returns>
    private static Dictionary<string, string> ExistingNames(string folder)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.GetFiles(folder)
                         .Where(f => Path.GetExtension(f).Equals(BLUEPRINT_EXTENSION, StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                names.TryAdd(name, name);
            }
        }
        return names;
    }

    /// <exception cref="IOException">Every number up to <see cref="MAX_COPY_NUMBER"/> is taken.</exception>
    private static string FreeName(string name, Dictionary<string, string> existing)
    {
        for (var number = 2; number <= MAX_COPY_NUMBER; number++)
        {
            var candidate = $"{name} ({number})";
            if (!existing.ContainsKey(candidate))
            {
                return candidate;
            }
        }
        throw new IOException($"There are already {MAX_COPY_NUMBER} copies of {name}.");
    }

    private static void AddEntry(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(content);
    }
}
