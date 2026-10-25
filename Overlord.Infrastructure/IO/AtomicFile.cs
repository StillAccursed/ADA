namespace Overlord.Infrastructure.IO;

/// <summary>
/// Replaces a file in one step: write a temporary file beside it, flush it to disk, then rename it
/// over the original. A crash at any point leaves either the old file or the new one.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            write(stream);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
