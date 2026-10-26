using System.IO.Compression;
using System.Text;

namespace ADA.Tests.Fixtures;

/// <summary>Builds small <c>.smod</c> archives for install tests.</summary>
public sealed class SmodBuilder(string reference, string version)
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private bool _gameFeature;
    private string? _platformFolder;
    private string? _rawUPlugin;

    public SmodBuilder GameFeature(bool value = true)
    {
        _gameFeature = value;
        return this;
    }

    /// <summary>Use the multi-target layout, with everything inside this folder (e.g. "Windows").</summary>
    public SmodBuilder InFolder(string platformFolder)
    {
        _platformFolder = platformFolder;
        return this;
    }

    public SmodBuilder File(string path, string content)
    {
        _files[path] = content;
        return this;
    }

    public SmodBuilder RawUPlugin(string json)
    {
        _rawUPlugin = json;
        return this;
    }

    /// <summary>A typical mod: binaries, paks and an icon, with contents that differ per version.</summary>
    public SmodBuilder Typical() => this
        .File($"Binaries/Win64/FactoryGameSteam-{reference}-Win64-Shipping.dll", $"dll {reference} {version}")
        .File($"Binaries/Win64/FactoryGameSteam-{reference}-Win64-Shipping.pdb", $"pdb {reference} {version}")
        .File($"Content/Paks/Windows/{reference}FactoryGame-Windows.pak", $"pak {reference} {version} " + new string('p', 2000))
        .File("Resources/Icon128.png", $"png {reference}");

    public string WriteTo(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{reference}-{version}.smod");
        var prefix = _platformFolder is null ? "" : _platformFolder + "/";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var uplugin = _rawUPlugin ?? $$"""
            {
            	"FileVersion": 3,
            	"SemVersion": "{{version}}",
            	"FriendlyName": "{{reference}} mod",
            	"GameVersion": ">=513885",
            	"Plugins": [ { "Name": "SML", "Enabled": true, "SemVersion": "^3.12.0" } ],
            	"GameFeature": {{(_gameFeature ? "true" : "false")}},
            }
            """;
        Add(zip, $"{prefix}{reference}.uplugin", uplugin);
        foreach (var (file, content) in _files)
            Add(zip, prefix + file, content);
        return path;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
