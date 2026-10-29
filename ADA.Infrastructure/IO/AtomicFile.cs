namespace ADA.Infrastructure.IO;

/// <summary>
/// Replaces a file in one step: write a temporary file beside it, flush it to disk, then rename it
/// over the original. A crash at any point leaves either the old file or the new one.
/// </summary>
internal static class AtomicFile
{
    public static void Write(string path, Action<Stream> write) => WriteAll([(path, write)]);

    /// <summary>
    /// <see cref="Write(string, Action{Stream})"/> for a file inside a game install, which may be
    /// on another machine: write a temporary file beside it, make it durable, then move it over.
    /// </summary>
    public static void Write(IGameFiles files, string path, Action<Stream> write)
    {
        var temp = path + ".tmp";
        files.CreateDirectory(GamePaths.Parent(path));
        // A crash mid-write leaves the temporary file behind; CreateNew refuses to reuse it.
        files.DeleteFile(temp);
        try
        {
            using (var stream = files.CreateNew(temp))
            {
                write(stream);
            }
            files.ReplaceFile(temp, path);
        }
        finally
        {
            if (files.FileExists(temp))
            {
                files.DeleteFile(temp);
            }
        }
    }

    /// <summary>
    /// Writes every file to its temporary name first, and renames them into place only once all of
    /// them were written, so a failure while writing leaves none of them changed.
    /// </summary>
    public static void WriteAll(IReadOnlyList<(string Path, Action<Stream> Write)> files)
    {
        var temps = new List<string>(files.Count);
        try
        {
            foreach (var (path, write) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var temp = path + ".tmp";
                temps.Add(temp);
                using var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            for (var i = 0; i < files.Count; i++)
            {
                File.Move(temps[i], files[i].Path, overwrite: true);
            }
        }
        finally
        {
            foreach (var temp in temps.Where(File.Exists))
            {
                File.Delete(temp);
            }
        }
    }
}
