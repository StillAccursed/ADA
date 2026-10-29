using System.Text;
using ADA.Infrastructure.IO;

namespace ADA.Tests.Installing;

/// <summary>
/// What the install engine relies on from any <see cref="IGameFiles"/>: run against the local and
/// SFTP implementations by <see cref="LocalGameFilesTests"/> and the SFTP tests. Arranges files
/// only through the interface, so it works wherever the files are.
/// </summary>
internal sealed class GameFilesContract(IGameFiles files)
{
    public void MoveDirectoryRefusesToOverwriteAnExistingFolder()
    {
        Write("from/a.txt", "new");
        Write("to/a.txt", "old");

        Assert.ThrowsAny<IOException>(() => files.MoveDirectory("from", "to"));
        Assert.Equal("old", Read("to/a.txt"));
    }

    public void MoveDirectoryRenamesAFolder()
    {
        Write("from/sub/a.txt", "x");

        files.MoveDirectory("from", "to");

        Assert.False(files.DirectoryExists("from"));
        Assert.Equal("x", Read("to/sub/a.txt"));
    }

    public void CreateNewRefusesToReplaceAnExistingFile()
    {
        Write("a.txt", "old");

        Assert.ThrowsAny<IOException>(() => files.CreateNew("a.txt"));
        Assert.Equal("old", Read("a.txt"));
    }

    public void ReplaceFileOverwritesTheTarget()
    {
        Write("a.tmp", "new");
        Write("a.txt", "old");

        files.ReplaceFile("a.tmp", "a.txt");

        Assert.Equal("new", Read("a.txt"));
        Assert.False(files.FileExists("a.tmp"));
    }

    public void DeleteFileIgnoresAMissingFile()
    {
        files.DeleteFile("missing.txt");

        Assert.False(files.FileExists("missing.txt"));
    }

    public void DeleteDirectoryRemovesEverythingInIt()
    {
        Write("mod/sub/a.txt", "x");
        Write("mod/b.txt", "y");

        files.DeleteDirectory("mod");

        Assert.False(files.DirectoryExists("mod"));
    }

    public void CreateDirectoryMakesMissingParents()
    {
        files.CreateDirectory("a/b/c");
        files.CreateDirectory("a/b/c");

        Assert.True(files.DirectoryExists("a/b/c"));
        Assert.False(files.FileExists("a/b/c"));
    }

    public void LockIsExclusiveUntilDisposed()
    {
        files.CreateDirectory(".ada");
        var held = files.AcquireLock(".ada/lock");

        Assert.ThrowsAny<IOException>(() => files.AcquireLock(".ada/lock"));

        held.Dispose();
        files.AcquireLock(".ada/lock").Dispose();
    }

    public void FilesListsEveryFileRelativeToTheFolderWithForwardSlashes()
    {
        Write("Mods/A/A.uplugin", "{}");
        Write("Mods/A/Content/Paks/A.pak", "1234");

        var listed = files.Files("Mods/A").OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        Assert.Equal(["A.uplugin", "Content/Paks/A.pak"], listed.Select(f => f.Path));
        Assert.Equal(4, listed[1].Length);
        Assert.Equal(4, files.FileInfo("Mods/A/Content/Paks/A.pak")!.Length);
    }

    public void DirectoryNamesListsOnlyTheImmediateFolders()
    {
        Write("Mods/A/x.txt", "");
        Write("Mods/GameFeatures/B/y.txt", "");
        Write("Mods/file.txt", "");

        Assert.Equal(["A", "GameFeatures"], files.DirectoryNames("Mods").Order(StringComparer.Ordinal));
    }

    public void FileInfoIsNullForAMissingFileOrAFolder()
    {
        files.CreateDirectory("folder");

        Assert.Null(files.FileInfo("missing.txt"));
        Assert.Null(files.FileInfo("folder"));
    }

    public void PathsThatWouldLeaveFactoryGameAreRefused()
    {
        foreach (var path in new[] { "../outside", "Mods/../../outside", "Mods/./A", "/etc/passwd", "Mods\\A", "C:outside" })
        {
            Assert.Throws<ArgumentException>(() => files.FileExists(path));
            Assert.Throws<ArgumentException>(() => files.CreateDirectory(path));
        }
    }

    public void AtomicWriteReplacesAFileAndLeavesNoTemporaryFile()
    {
        AtomicFile.Write(files, ".ada/installed.json", stream => stream.Write("first"u8));
        AtomicFile.Write(files, ".ada/installed.json", stream => stream.Write("second"u8));

        Assert.Equal("second", Read(".ada/installed.json"));
        Assert.False(files.FileExists(".ada/installed.json.tmp"));
    }

    private void Write(string path, string content)
    {
        files.CreateDirectory(GamePaths.Parent(path));
        using var stream = files.CreateNew(path);
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private string Read(string path)
    {
        using var reader = new StreamReader(files.OpenRead(path));
        return reader.ReadToEnd();
    }
}
