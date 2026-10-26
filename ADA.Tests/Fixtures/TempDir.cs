namespace ADA.Tests.Fixtures;

/// <summary>A throwaway folder for building fake game installs, bottles and Steam libraries.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("ada-tests-").FullName;

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relativePath, string content = "")
    {
        var full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Creates the minimum folder layout <c>GameInstallValidator</c> accepts.</summary>
    public string CreateGame(string relativePath, string executable = "FactoryGameEGS.exe")
    {
        var game = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.Combine(game, "FactoryGame", "Binaries", "Win64"));
        File.WriteAllText(System.IO.Path.Combine(game, executable), "");
        return game;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
