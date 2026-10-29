using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ADA.Core.Catalog;
using ADA.Core.Games;
using ADA.Core.Servers;
using ADA.Infrastructure.Installing;
using ADA.Infrastructure.IO;
using ADA.Infrastructure.Servers;
using ADA.Tests.Fixtures;
using ADA.Tests.Installing;
using FluentFTP.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ADA.Tests.Servers;

/// <summary>FTPS servers, through an in-memory FTP session: the file contract, the folder lock, the engine and the setup check.</summary>
public sealed class FtpGameFilesTests : IDisposable
{
    private const string ROOT = "/srv/satisfactory";

    private readonly TempDir _dir = new();
    private readonly InMemoryFtpSession _session = new();
    private readonly FtpGameFiles _files;
    private readonly GameFilesContract _contract;

    public FtpGameFilesTests()
    {
        _session.CreateDirectories($"{ROOT}/FactoryGame");
        _files = new FtpGameFiles(_session, ROOT, NullLogger.Instance);
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
    public void The_lock_is_a_folder_that_names_its_holder_and_can_be_taken_over()
    {
        _files.CreateDirectory(GameModFolders.WORK);
        using (_files.AcquireLock(GameModFolders.LOCK))
        {
            Assert.True(_session.DirectoryExists($"{ROOT}/FactoryGame/.ada/lock.d"));
            Assert.Contains(Environment.MachineName, _session.Read($"{ROOT}/FactoryGame/.ada/lock.d/holder.json"));
        }
        Assert.False(_session.DirectoryExists($"{ROOT}/FactoryGame/.ada/lock.d"));

        _session.CreateDirectories($"{ROOT}/FactoryGame/.ada/lock.d");
        _session.Put($"{ROOT}/FactoryGame/.ada/lock.d/holder.json",
            JsonSerializer.Serialize(new LockHolder("OTHER-PC", 1234, DateTimeOffset.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var lockFile = new FtpLockFile(_session, $"{ROOT}/FactoryGame/{GameModFolders.LOCK}");

        var e = Assert.Throws<LockHeldException>(() => _files.AcquireLock(GameModFolders.LOCK));
        Assert.StartsWith("OTHER-PC has been changing this server's mods since", e.Message);
        Assert.Equal("OTHER-PC", RemoteLock.HeldByOther(lockFile)?.Machine);

        RemoteLock.Break(lockFile);
        _files.AcquireLock(GameModFolders.LOCK).Dispose();
    }

    [Fact]
    public void A_lock_left_by_an_exited_process_on_this_pc_is_taken_over()
    {
        _session.CreateDirectories($"{ROOT}/FactoryGame/.ada/lock.d");
        _session.Put($"{ROOT}/FactoryGame/.ada/lock.d/holder.json",
            JsonSerializer.Serialize(new LockHolder(Environment.MachineName, int.MaxValue, DateTimeOffset.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        _files.AcquireLock(GameModFolders.LOCK).Dispose();

        Assert.False(_session.DirectoryExists($"{ROOT}/FactoryGame/.ada/lock.d"));
    }

    [Fact]
    public void A_lock_whose_holder_cant_be_written_leaves_no_folder_behind()
    {
        _files.CreateDirectory(GameModFolders.WORK);
        _session.FailUploadsEnding = "holder.json";

        Assert.ThrowsAny<IOException>(() => _files.AcquireLock(GameModFolders.LOCK));

        Assert.False(_session.DirectoryExists($"{ROOT}/FactoryGame/.ada/lock.d"));
    }

    [Fact]
    public void Mods_install_check_and_uninstall_over_ftp()
    {
        var installer = new ModInstaller(_files, () => false, checkpoint: null);
        var smod = new SmodBuilder("SML", "3.12.0").Typical().WriteTo(_dir.Combine("Published"));

        installer.Apply([new ModChange.Install(smod, TargetPlatform.LinuxServer)]);

        Assert.True(_session.FileExists($"{ROOT}/FactoryGame/Mods/SML/SML.uplugin"));
        Assert.All(installer.Check(full: true).Mods, m => Assert.Equal(ModHealth.Ok, m.Health));
        Assert.All(installer.Check().Mods, m => Assert.Equal(ModHealth.Ok, m.Health));

        installer.Apply([new ModChange.Uninstall("SML")]);
        Assert.Empty(installer.Installed);
        Assert.False(_session.DirectoryExists($"{ROOT}/FactoryGame/Mods/SML"));
        Assert.Empty(_files.DirectoryNames(GameModFolders.WORK));
    }

    private void PlaceServer()
    {
        _session.Put($"{ROOT}/FactoryServer.sh", "#!/bin/sh");
        _session.Put($"{ROOT}/Engine/Binaries/Linux/FactoryServer-Linux-Shipping", "ELF server program");
        _session.Put($"{ROOT}/Engine/Binaries/Linux/FactoryServer-Linux-Shipping.version", """{ "Changelist": 513885, "GameVersion": "1.2.4.1" }""");
    }

    [Fact]
    public void The_setup_check_accepts_a_server_that_replaces_in_one_step()
    {
        PlaceServer();
        var steps = new List<SetupStep>();

        var (root, host, build) = ServerSetupCheck.CheckFiles(new FtpServerFolders(_session), "/srv", steps.Add);

        Assert.True(steps.All(s => s.Passed || !s.Blocking), string.Join("\n", steps.Select(s => $"{s.Name}: {s.Detail}")));
        Assert.Equal(ROOT, root);
        Assert.Equal(GameHost.Linux, host);
        Assert.Equal(513885, build?.Changelist);
        Assert.Empty(_session.List($"{ROOT}/FactoryGame"));
    }

    [Fact]
    public void The_setup_check_refuses_a_server_that_cant_replace_a_file_or_is_a_game_install()
    {
        PlaceServer();
        _session.RenameReplaces = false;
        var steps = new List<SetupStep>();

        ServerSetupCheck.CheckFiles(new FtpServerFolders(_session), ROOT, steps.Add);

        Assert.Equal("Safe file replace", steps[^1].Name);
        Assert.StartsWith("This FTP server can't replace a file in one step", steps[^1].Detail);

        _session.Put($"{ROOT}/FactoryGameSteam.exe", "client");
        steps.Clear();
        ServerSetupCheck.CheckFiles(new FtpServerFolders(_session), ROOT, steps.Add);
        Assert.StartsWith("This is a game install", steps[^1].Detail);
    }

    [Fact]
    public void Connection_problems_name_the_cause()
    {
        var server = new ServerConnection(Guid.NewGuid(), "Home", "ftp.example", 21, "steam", ServerAuth.Password, ROOT, GameHost.Linux, "abc",
            Transport: ServerTransport.Ftp);

        Assert.StartsWith("This server only offers unencrypted FTP",
            ServerConnections.FtpConnectProblem(server, new FtpSecurityNotAvailableException(), identityChanged: false));
        Assert.Equal("Home refused the login. Check the user name and password.",
            ServerConnections.FtpConnectProblem(server, new FtpAuthenticationException(new FluentFTP.FtpReply()), identityChanged: false));
        Assert.StartsWith("Home's certificate has changed",
            ServerConnections.FtpConnectProblem(server, new IOException("handshake"), identityChanged: true));
    }

    [Fact]
    public void A_certificates_fingerprint_is_stable_and_differs_between_certificates()
    {
        using var first = SelfSigned("CN=first");
        using var second = SelfSigned("CN=second");

        var fingerprint = ServerConnections.CertificateFingerprint(first);

        Assert.Equal(fingerprint, ServerConnections.CertificateFingerprint(X509CertificateLoader.LoadCertificate(first.RawData)));
        Assert.NotEqual(fingerprint, ServerConnections.CertificateFingerprint(second));
        Assert.Equal(43, fingerprint.Length); // 32 bytes, base64 without padding, like an SSH fingerprint
    }

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = ECDsa.Create();
        return new CertificateRequest(subject, key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [FtpFact]
    public async Task A_real_ftps_server_passes_the_file_contract_and_the_engine()
    {
        using var connections = new ServerConnections(new ServerStore(_dir.Combine("ftp-servers.json")),
            new ServerSecrets(new FakeSecretStore()), new SatisfactoryServerApi(new HttpClient(new FakeHttpHandler())));
        using var server = await FtpTestServer.StartAsync(connections);
        var files = new FtpGameFiles(server.Session, server.Server.GameRoot, NullLogger.Instance);
        var contract = new GameFilesContract(files);

        contract.CreateNewRefusesToReplaceAnExistingFile();
        contract.ReplaceFileOverwritesTheTarget();
        contract.MoveDirectoryRenamesAFolder();
        contract.LockIsExclusiveUntilDisposed();
        contract.AtomicWriteReplacesAFileAndLeavesNoTemporaryFile();

        var installer = new ModInstaller(files, () => false, checkpoint: null);
        installer.Apply([new ModChange.Install(new SmodBuilder("SML", "3.12.0").Typical().WriteTo(_dir.Combine("Real")), TargetPlatform.LinuxServer)]);
        Assert.All(installer.Check(full: true).Mods, m => Assert.Equal(ModHealth.Ok, m.Health));
        installer.Apply([new ModChange.Uninstall("SML")]);
        Assert.Empty(installer.Installed);
    }

    [Fact]
    public void A_lock_folder_without_a_holder_is_held_by_an_unknown_ada_and_can_be_taken_over()
    {
        _session.CreateDirectories($"{ROOT}/FactoryGame/.ada/lock.d");
        var lockFile = new FtpLockFile(_session, $"{ROOT}/FactoryGame/{GameModFolders.LOCK}");

        var e = Assert.Throws<LockHeldException>(() => _files.AcquireLock(GameModFolders.LOCK));

        Assert.StartsWith("This server's mods were left mid-change", e.Message);
        Assert.Same(LockHolder.UNKNOWN, RemoteLock.HeldByOther(lockFile));
        RemoteLock.Break(lockFile);
        _files.AcquireLock(GameModFolders.LOCK).Dispose();
    }

    [Fact]
    public void Mods_taken_over_on_ftp_pass_the_quick_check_without_downloading()
    {
        _session.Put($"{ROOT}/FactoryGame/Mods/Storage/Storage.uplugin", """{ "SemVersion": "1.0.0", "FriendlyName": "Storage" }""");
        _session.Put($"{ROOT}/FactoryGame/Mods/Storage/Binaries/Linux/libFactoryServer-Storage-Linux-Shipping.so", "library");
        var installer = new ModInstaller(_files, () => false, checkpoint: null);
        installer.Adopt(installer.FindUnmanaged(TargetPlatform.LinuxServer).Mods);
        _session.Downloads.Clear();

        var check = installer.Check();

        Assert.Equal(ModHealth.Ok, Assert.Single(check.Mods).Health);
        Assert.DoesNotContain(_session.Downloads, path => path.Contains("/Mods/", StringComparison.Ordinal)); // only ADA's own records
    }
}
