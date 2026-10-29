using System.Text;
using System.Text.Json;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Servers;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Servers;

/// <summary>The install engine over a real SFTP server; skipped unless one is configured (see <see cref="SftpTestServer"/>).</summary>
public sealed class SftpGameFilesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SftpTestServer? _server;
    private readonly SftpGameFiles? _files;

    public SftpGameFilesTests()
    {
        if (SftpTestServer.Settings is not null)
        {
            _server = new SftpTestServer();
            _files = new SftpGameFiles(_server.Client, _server.Server.GameRoot, NullLogger.Instance);
        }
    }

    public void Dispose()
    {
        _server?.Dispose();
        _dir.Dispose();
    }

    private GameFilesContract Contract => new(_files!);

    [SftpFact]
    public void MoveDirectoryRefusesToOverwriteAnExistingFolder() => Contract.MoveDirectoryRefusesToOverwriteAnExistingFolder();

    [SftpFact]
    public void MoveDirectoryRenamesAFolder() => Contract.MoveDirectoryRenamesAFolder();

    [SftpFact]
    public void CreateNewRefusesToReplaceAnExistingFile() => Contract.CreateNewRefusesToReplaceAnExistingFile();

    [SftpFact]
    public void ReplaceFileOverwritesTheTarget() => Contract.ReplaceFileOverwritesTheTarget();

    [SftpFact]
    public void DeleteFileIgnoresAMissingFile() => Contract.DeleteFileIgnoresAMissingFile();

    [SftpFact]
    public void DeleteDirectoryRemovesEverythingInIt() => Contract.DeleteDirectoryRemovesEverythingInIt();

    [SftpFact]
    public void CreateDirectoryMakesMissingParents() => Contract.CreateDirectoryMakesMissingParents();

    [SftpFact]
    public void LockIsExclusiveUntilDisposed() => Contract.LockIsExclusiveUntilDisposed();

    [SftpFact]
    public void FilesListsEveryFileRelativeToTheFolderWithForwardSlashes() => Contract.FilesListsEveryFileRelativeToTheFolderWithForwardSlashes();

    [SftpFact]
    public void DirectoryNamesListsOnlyTheImmediateFolders() => Contract.DirectoryNamesListsOnlyTheImmediateFolders();

    [SftpFact]
    public void FileInfoIsNullForAMissingFileOrAFolder() => Contract.FileInfoIsNullForAMissingFileOrAFolder();

    [SftpFact]
    public void PathsThatWouldLeaveFactoryGameAreRefused() => Contract.PathsThatWouldLeaveFactoryGameAreRefused();

    [SftpFact]
    public void AtomicWriteReplacesAFileAndLeavesNoTemporaryFile() => Contract.AtomicWriteReplacesAFileAndLeavesNoTemporaryFile();

    [SftpFact]
    public void InstallsUpdatesChecksAndUninstallsOverSftp() => InstallUpdateCheckUninstall(readBack: true);

    [SftpFact]
    public void InstallsOverSftpCheckingSizesOnlyAndAFullCheckStillPasses() => InstallUpdateCheckUninstall(readBack: false);

    private void InstallUpdateCheckUninstall(bool readBack)
    {
        var installer = new ModInstaller(_files!, () => false, checkpoint: null, readBack: readBack);
        var v1 = new SmodBuilder("SML", "3.12.0").Typical().WriteTo(_dir.Combine("1"));
        var v2 = new SmodBuilder("SML", "3.12.1").Typical().File("Content/New.pak", "new").WriteTo(_dir.Combine("2"));

        installer.Apply([new ModChange.Install(v1, TargetPlatform.WindowsServer)]);
        installer.Apply([new ModChange.Install(v2, TargetPlatform.WindowsServer)]);

        Assert.Equal("3.12.1", installer.Installed["SML"].Version);
        Assert.True(_files!.FileExists("Mods/SML/Content/New.pak"));
        Assert.All(installer.Check(full: true).Mods, m => Assert.Equal(ModHealth.Ok, m.Health));
        Assert.Equal([".ada"], _files.DirectoryNames("").Where(n => n.StartsWith('.')));

        installer.Apply([new ModChange.Uninstall("SML")]);

        Assert.Empty(installer.Installed);
        Assert.False(_files.DirectoryExists("Mods/SML"));
        Assert.Empty(_files.DirectoryNames(GameModFolders.WORK));
    }

    [SftpFact]
    public void ALockFromAnotherPcSaysWhoHoldsIt()
    {
        WriteLockHolder(new LockHolder("OTHER-PC", 1234, DateTimeOffset.UtcNow));

        var e = Assert.Throws<LockHeldException>(() => _files!.AcquireLock(GameModFolders.LOCK));

        Assert.StartsWith("OTHER-PC has been changing this server's mods since", e.Message);
    }

    [SftpFact]
    public void ALockLeftByAnExitedProcessOnThisPcIsTakenOver()
    {
        // Process ids are positive and far below this on every OS ADA runs on.
        WriteLockHolder(new LockHolder(Environment.MachineName, int.MaxValue, DateTimeOffset.UtcNow.AddHours(-1)));

        using var held = _files!.AcquireLock(GameModFolders.LOCK);

        Assert.True(_files.FileExists(GameModFolders.LOCK));
    }

    [SftpFact]
    public async Task SetupCheckFindsTheServerBelowTheFolderEnteredAndReadsItsBuild()
    {
        // The fixture puts FactoryGame directly in its folder, so search from a subfolder that has none.
        var entered = $"{_server!.Server.GameRoot}/search";
        var root = $"{entered}/steamapps/common/SatisfactoryDedicatedServer";
        foreach (var folder in new[] { "", "/steamapps", "/steamapps/common", "/steamapps/common/SatisfactoryDedicatedServer",
                     "/steamapps/common/SatisfactoryDedicatedServer/FactoryGame", "/steamapps/common/SatisfactoryDedicatedServer/Engine",
                     "/steamapps/common/SatisfactoryDedicatedServer/Engine/Binaries",
                     "/steamapps/common/SatisfactoryDedicatedServer/Engine/Binaries/Linux" })
        {
            _server.Client.CreateDirectory(entered + folder);
        }
        Upload($"{root}/FactoryServer.sh", "#!/bin/sh");
        Upload($"{root}/Engine/Binaries/Linux/FactoryServer-Linux-Shipping", "ELF server program");
        Upload($"{root}/Engine/Binaries/Linux/FactoryServer-Linux-Shipping.version", """{ "Changelist": 513885, "GameVersion": "1.2.4.1" }""");

        using var connections = Connections(out var api);
        var result = await new ServerSetupCheck(connections, api).RunAsync(_server.Server with { GameRoot = entered }, SftpTestServer.Password);

        Assert.True(result.Passed, string.Join("\n", result.Steps.Select(s => $"{s.Name}: {s.Detail}")));
        Assert.Equal(root, result.GameRoot);
        Assert.Equal(GameHost.Linux, result.Host);
        Assert.Equal(513885, result.Build?.Changelist);
        Assert.False(string.IsNullOrEmpty(result.HostKeyFingerprint));
        Assert.Contains(result.Steps, s => s is { Name: "Server API", Passed: false, Blocking: false });
    }

    [SftpFact]
    public async Task SetupCheckFailsWhenNoServerIsInTheFolder()
    {
        using var connections = Connections(out var api);

        var result = await new ServerSetupCheck(connections, api).RunAsync(
            _server!.Server with { GameRoot = $"{_server.Server.GameRoot}/FactoryGame" }, SftpTestServer.Password);

        Assert.False(result.Passed);
        Assert.Equal("Find the game", result.Steps[^1].Name);
    }

    [SftpFact]
    public async Task SetupCheckRefusesAGameInstallEvenWithAServerLauncherAdded()
    {
        var root = _server!.Server.GameRoot;
        Upload($"{root}/FactoryGameSteam.exe", "client");
        Upload($"{root}/FactoryServer.exe", "");

        using var connections = Connections(out var api);
        var result = await new ServerSetupCheck(connections, api).RunAsync(_server.Server, SftpTestServer.Password);

        Assert.False(result.Passed);
        Assert.StartsWith("This is a game install, not a dedicated server.", result.Steps[^1].Detail);
    }

    [SftpFact]
    public async Task TheConnectedServersLockCanBeReadAndTakenOver()
    {
        var store = new ServerStore(_dir.Combine("lock-servers.json"));
        var secrets = new FakeSecretStore();
        store.Save(_server!.Server);
        secrets.Saved[$"server:{_server.Server.Id:N}"] = SftpTestServer.Password;
        using var connections = Connections(store, secrets);
        WriteLockHolder(new LockHolder("OTHER-PC", 1234, DateTimeOffset.UtcNow));

        await connections.ConnectAsync(_server.Server.Id);
        Assert.Equal("OTHER-PC", (await connections.LockHolderAsync())?.Machine);

        await connections.BreakLockAsync();
        Assert.Null(await connections.LockHolderAsync());
        Assert.False(_files!.FileExists(GameModFolders.LOCK));
    }

    [SftpFact]
    public void FreeSpaceMatchesThisMachineWhenTheServerIsLocalhost()
    {
        if (_server!.Server.Address is not ("localhost" or "127.0.0.1" or "::1"))
        {
            return;
        }

        var sftp = _files!.AvailableFreeSpace();
        var local = DiskSpace.DriveFor(_server.Server.GameRoot)!.AvailableFreeSpace;

        // Other programs write while the test runs, so allow a little drift.
        Assert.NotNull(sftp);
        Assert.InRange(sftp.Value, local * 0.99, local * 1.01);
    }

    private static ServerConnections Connections(ServerStore store, FakeSecretStore secrets) => new(store, new ServerSecrets(secrets),
        new SatisfactoryServerApi(new HttpClient(new FakeHttpHandler().Throw(new HttpRequestException("No server API.")))));

    private ServerConnections Connections(out SatisfactoryServerApi api)
    {
        api = new SatisfactoryServerApi(new HttpClient(new FakeHttpHandler().Throw(new HttpRequestException("No server API."))));
        return new ServerConnections(new ServerStore(_dir.Combine("servers.json")), new ServerSecrets(new FakeSecretStore()), api);
    }

    private void WriteLockHolder(LockHolder holder)
    {
        _files!.CreateDirectory(GameModFolders.WORK);
        using var stream = _files.CreateNew(GameModFolders.LOCK);
        JsonSerializer.Serialize(stream, holder, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private void Upload(string path, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        _server!.Client.UploadFile(stream, path);
    }
}
