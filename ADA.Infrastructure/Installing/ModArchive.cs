using System.IO.Compression;
using ADA.Core.Catalog;

namespace ADA.Infrastructure.Installing;

/// <summary>A downloaded <c>.smod</c> isn't a safe, well-formed mod archive; nothing is extracted from it.</summary>
internal sealed class InvalidModArchiveException(string message) : Exception(message);

/// <summary>One file of the mod, relative to the mod's own folder, with <c>/</c> separators.</summary>
internal sealed record ModArchiveEntry(string RelativePath, long Size, ZipArchiveEntry Zip);

/// <summary>
/// A <c>.smod</c> opened for one target platform. Handles both layouts from <c>smod.md</c>: flat
/// (ficsit.app downloads, <c>.uplugin</c> at the root) and multi-target (a folder per platform).
/// Every safety check runs when the archive is opened, before anything is extracted.
/// </summary>
internal sealed class ModArchive : IDisposable
{
    /// <summary>
    /// A sanity ceiling per mod, far above any real one (whole profiles reach tens of GB, single
    /// mods a few). Zip bombs are caught by <see cref="MAX_COMPRESSION_RATIO"/>, by the free-space
    /// check, and by extraction stopping at each entry's declared size.
    /// </summary>
    public const long MAX_UNCOMPRESSED_BYTES = 64L * 1024 * 1024 * 1024;

    /// <summary>
    /// Deflate tops out near 1032:1, which only a bomb's repeated bytes reach. Real mod files
    /// (textures, paks, binaries) compress far less, so anything this dense is refused.
    /// </summary>
    public const int MAX_COMPRESSION_RATIO = 200;

    // Small entries can legitimately compress well (an empty config, padding); only judge large ones.
    private const long RATIO_CHECK_MINIMUM_BYTES = 16L * 1024 * 1024;

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
            {
                throw new InvalidModArchiveException(prefix == ""
                    ? "No .uplugin at the archive root."
                    : $"No .uplugin at the archive root, and no '{platform}' folder with one.");
            }

            if (uplugins.Count > 1)
            {
                throw new InvalidModArchiveException("The mod root has more than one .uplugin.");
            }

            var reference = Path.GetFileNameWithoutExtension(uplugins[0].Path[prefix.Length..]);
            UPluginDescriptor descriptor;
            using (var stream = uplugins[0].Entry.Open())
            {
                descriptor = UPluginDescriptor.Parse(reference, stream);
            }

            var entries = new List<ModArchiveEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, entryPath) in all)
            {
                if (!entryPath.StartsWith(prefix, StringComparison.Ordinal) || entryPath.EndsWith('/'))
                {
                    continue;
                }

                var relative = entryPath[prefix.Length..];
                if (!includeDebugSymbols && relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Two names that differ only by case would overwrite each other on macOS and Windows.
                if (!seen.Add(relative))
                {
                    throw new InvalidModArchiveException($"The archive contains '{relative}' more than once.");
                }

                if (entry.Length >= RATIO_CHECK_MINIMUM_BYTES && entry.Length > (long)MAX_COMPRESSION_RATIO * Math.Max(entry.CompressedLength, 1))
                {
                    throw new InvalidModArchiveException($"'{relative}' is compressed more than {MAX_COMPRESSION_RATIO}:1, which real mod files never are.");
                }

                entries.Add(new ModArchiveEntry(relative, entry.Length, entry));
            }

            var total = entries.Sum(e => e.Size);
            if (total > MAX_UNCOMPRESSED_BYTES)
            {
                throw new InvalidModArchiveException($"The mod would extract to {total:N0} bytes, over the {MAX_UNCOMPRESSED_BYTES:N0} byte limit.");
            }

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
