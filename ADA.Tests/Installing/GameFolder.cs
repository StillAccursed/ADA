using System.IO.Compression;
using System.Text;

namespace ADA.Tests.Installing;

/// <summary>A fake game install plus helpers to read back what is in its Mods folders.</summary>
internal sealed class GameFolder
{
    public string Root { get; }
    public string FactoryGame => Path.Combine(Root, "FactoryGame");
    public string Mods => Path.Combine(FactoryGame, "Mods");
    public string Work => Path.Combine(FactoryGame, ".ada");

    public GameFolder(string root)
    {
        Root = root;
        Directory.CreateDirectory(Path.Combine(FactoryGame, "Binaries", "Win64"));
        Directory.CreateDirectory(Mods);
    }

    public string ModFolder(string location) => Path.Combine(FactoryGame, location.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Every file under a mod folder: relative path → content. Null if the folder is absent.</summary>
    public Dictionary<string, string>? Read(string location)
    {
        var folder = ModFolder(location);
        if (!Directory.Exists(folder))
            return null;
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllText);
    }

    /// <summary>What a flat archive should produce once installed: relative path → content.</summary>
    public static Dictionary<string, string> Expected(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        return zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        });
    }

    public IEnumerable<string> WorkFolderEntries() =>
        Directory.Exists(Work)
            ? Directory.EnumerateFileSystemEntries(Work).Select(Path.GetFileName).OfType<string>().Order()
            : [];
}
