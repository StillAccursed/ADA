using System.IO.Compression;
using Overlord.Core.Catalog;

namespace Overlord.Infrastructure.Installing;

public sealed class InvalidModArchiveException(string message) : Exception(message);

/// <summary>One file of the mod, relative to the mod's own folder, with <c>/</c> separators.</summary>
public sealed record ModArchiveEntry(string RelativePath, long Size, ZipArchiveEntry Zip);

/// <summary>
/// A <c>.smod</c> opened for one target platform. Handles both layouts from <c>smod.md</c>: flat
/// (ficsit.app downloads, <c>.uplugin</c> at the root) and multi-target (a folder per platform).
/// Every safety check runs when the archive is opened, before anything is extracted.
/// </summary>
public sealed class ModArchive : IDisposable
{
    /// <summary>Guards against zip bombs: no Satisfactory mod comes close to this uncompressed.</summary>
    public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;

    private readonly ZipArchive _zip;

    public UPluginDescriptor Descriptor { get; }

    public IReadOnlyList<ModArchiveEntry> Entries { get; }

    public long UncompressedBytes { get; }

    private ModArchive(ZipArchive zip, UPluginDescriptor descriptor, IReadOnlyList<ModArchiveEntry> entries)
    {
        _zip = zip;
        Descriptor = descriptor;
        Entries = entries;
        UncompressedBytes = entries.Sum(e => e.Size);
    }

    /// <param name="includeDebugSymbols">False skips <c>*.pdb</c> files, which the game does not need.</param>
    /// <exception cref="InvalidModArchiveException">The archive fails any check in <c>smod.md</c>.</exception>
    public static ModArchive Open(string path, TargetPlatform platform, bool includeDebugSymbols = true)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidModArchiveException($"{Path.GetFileName(path)} is not a valid zip archive: {e.Message}");
        }

        try
        {
            var all = zip.Entries.Select(e => (Entry: e, Path: SafePath(e.FullName))).ToList();

            // Flat if a .uplugin sits at the root; otherwise look inside the platform's folder.
            var prefix = all.Any(e => IsRootUPlugin(e.Path, "")) ? "" : platform + "/";
            var uplugins = all.Where(e => IsRootUPlugin(e.Path, prefix)).ToList();
            if (uplugins.Count == 0)
                throw new InvalidModArchiveException(prefix == ""
                    ? "No .uplugin at the archive root."
                    : $"No .uplugin at the archive root, and no '{platform}' folder with one.");
            if (uplugins.Count > 1)
                throw new InvalidModArchiveException("The mod root has more than one .uplugin.");

            var reference = Path.GetFileNameWithoutExtension(uplugins[0].Path[prefix.Length..]);
            UPluginDescriptor descriptor;
            using (var stream = uplugins[0].Entry.Open())
                descriptor = UPluginDescriptor.Parse(reference, stream);

            var entries = new List<ModArchiveEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, entryPath) in all)
            {
                if (!entryPath.StartsWith(prefix, StringComparison.Ordinal) || entryPath.EndsWith('/'))
                    continue;

                var relative = entryPath[prefix.Length..];
                if (!includeDebugSymbols && relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Two names that differ only by case would overwrite each other on macOS and Windows.
                if (!seen.Add(relative))
                    throw new InvalidModArchiveException($"The archive contains '{relative}' more than once.");

                entries.Add(new ModArchiveEntry(relative, entry.Length, entry));
            }

            var total = entries.Sum(e => e.Size);
            if (total > MaxUncompressedBytes)
                throw new InvalidModArchiveException($"The mod would extract to {total:N0} bytes, over the {MaxUncompressedBytes:N0} byte limit.");

            return new ModArchive(zip, descriptor, entries);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    public void Dispose() => _zip.Dispose();

    private static bool IsRootUPlugin(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal)
        && path.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase)
        && !path.AsSpan(prefix.Length).Contains('/');

    /// <summary>Rejects entries that would land outside the mod folder ("zip-slip").</summary>
    private static string SafePath(string fullName)
    {
        var path = fullName.Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':')
            || path.Split('/').Any(segment => segment is ".." or "."))
        {
            throw new InvalidModArchiveException($"Unsafe path in archive: '{fullName}'.");
        }
        return path;
    }
}
