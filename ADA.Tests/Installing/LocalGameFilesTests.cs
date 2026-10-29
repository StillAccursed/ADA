using ADA.Infrastructure.IO;
using ADA.Tests.Fixtures;

namespace ADA.Tests.Installing;

public sealed class LocalGameFilesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly LocalGameFiles _files;
    private readonly GameFilesContract _contract;

    public LocalGameFilesTests()
    {
        _files = new LocalGameFiles(_dir.Path);
        _contract = new GameFilesContract(_files);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void MoveDirectoryRefusesToOverwriteAnExistingFolder() => _contract.MoveDirectoryRefusesToOverwriteAnExistingFolder();

    [Fact]
    public void MoveDirectoryRenamesAFolder() => _contract.MoveDirectoryRenamesAFolder();

    [Fact]
    public void CreateNewRefusesToReplaceAnExistingFile() => _contract.CreateNewRefusesToReplaceAnExistingFile();

    [Fact]
    public void ReplaceFileOverwritesTheTarget() => _contract.ReplaceFileOverwritesTheTarget();

    [Fact]
    public void DeleteFileIgnoresAMissingFile() => _contract.DeleteFileIgnoresAMissingFile();

    [Fact]
    public void DeleteDirectoryRemovesEverythingInIt() => _contract.DeleteDirectoryRemovesEverythingInIt();

    [Fact]
    public void CreateDirectoryMakesMissingParents() => _contract.CreateDirectoryMakesMissingParents();

    [Fact]
    public void LockIsExclusiveUntilDisposed() => _contract.LockIsExclusiveUntilDisposed();

    [Fact]
    public void FilesListsEveryFileRelativeToTheFolderWithForwardSlashes() => _contract.FilesListsEveryFileRelativeToTheFolderWithForwardSlashes();

    [Fact]
    public void DirectoryNamesListsOnlyTheImmediateFolders() => _contract.DirectoryNamesListsOnlyTheImmediateFolders();

    [Fact]
    public void FileInfoIsNullForAMissingFileOrAFolder() => _contract.FileInfoIsNullForAMissingFileOrAFolder();

    [Fact]
    public void PathsThatWouldLeaveFactoryGameAreRefused() => _contract.PathsThatWouldLeaveFactoryGameAreRefused();

    [Fact]
    public void AtomicWriteReplacesAFileAndLeavesNoTemporaryFile() => _contract.AtomicWriteReplacesAFileAndLeavesNoTemporaryFile();

    [Fact]
    public void CreateNewKeepsWhatWasWrittenOnceDisposed()
    {
        using (var stream = _files.CreateNew("a.txt"))
        {
            stream.Write("hello"u8);
        }

        Assert.Equal("hello", File.ReadAllText(_dir.Combine("a.txt")));
    }

    [Fact]
    public void DeleteDirectoryRemovesReadOnlyFiles()
    {
        var file = _dir.WriteFile("mod/sub/a.txt", "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        _files.DeleteDirectory("mod");

        Assert.False(_files.DirectoryExists("mod"));
    }
}
