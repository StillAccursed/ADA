using System.Text.RegularExpressions;
using ADA.Core.Games;
using ADA.Core.Servers;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace ADA.Infrastructure.Servers;

/// <param name="Blocking">A failure means the server can't be added; otherwise it's a warning.</param>
public sealed record SetupStep(string Name, bool Passed, string Detail, bool Blocking = true);

/// <param name="HostKeyFingerprint">What the server presented, for the user to trust on first contact.</param>
/// <param name="GameRoot">The folder found, which may be below the one the user entered.</param>
/// <param name="Host">The server's OS, or null when it couldn't be told and the user must choose.</param>
public sealed record SetupResult(IReadOnlyList<SetupStep> Steps, string? HostKeyFingerprint, string? GameRoot,
    GameHost? Host, GameBuild? Build)
{
    public bool Passed => Steps.All(s => s.Passed || !s.Blocking);
}

/// <summary>
/// The Add Server page's Test Connection: logs in (for SFTP and FTPS), finds the server, and checks ADA can
/// manage mods there safely. Stops at the first blocking failure. Changes nothing except two
/// temporary files, which it removes.
/// </summary>
/// <remarks>
/// A game install is never accepted as a server, whatever files were added to it: a saved server
/// only ever gets server builds, and this check keeps a game client from being added by mistake or
/// with a renamed file (see the step 9 proposal).
/// </remarks>
public sealed partial class ServerSetupCheck(ServerConnections connections, SatisfactoryServerApi api)
{
    private const int MAX_SEARCH_DEPTH = 3;
    private const int MAX_SEARCHED_FOLDERS = 200;

    // Win64 holds Windows server builds; Linux the native Linux server.
    private static readonly (string Folder, GameHost Host)[] BINARY_FOLDERS = [("Linux", GameHost.Linux), ("Win64", GameHost.Windows)];

    // A game client's launchers, in the install folder.
    private static readonly string[] CLIENT_LAUNCHERS = ["FactoryGameSteam.exe", "FactoryGameEGS.exe", "FactoryGame.exe"];

    private const string GAME_INSTALL_PROBLEM =
        "This is a game install, not a dedicated server. ADA manages games installed through Steam or the Epic Games "
        + "Launcher, and dedicated servers on the Servers page.";

    /// <param name="draft">The details as entered. An empty host key fingerprint trusts whatever key the server presents.</param>
    /// <param name="secret">The password or key passphrase as typed; null uses the saved one. Unused for a folder.</param>
    /// <param name="detectedRoots">Installs ADA already lists; a folder server that's one of them gets a warning.</param>
    public async Task<SetupResult> RunAsync(ServerConnection draft, string? secret, IProgress<SetupStep>? progress = null,
        CancellationToken ct = default, IReadOnlyCollection<string>? detectedRoots = null)
    {
        var steps = new List<SetupStep>();
        void Report(SetupStep step)
        {
            steps.Add(step);
            progress?.Report(step);
        }

        if (draft.Transport == ServerTransport.Folder)
        {
            return await CheckFolderAsync(draft, detectedRoots ?? [], Report, steps, ct);
        }
        if (draft.Transport == ServerTransport.Ftp)
        {
            return await CheckFtpAsync(draft, secret, Report, steps, ct);
        }

        string? fingerprint = null;
        SftpClient client;
        try
        {
            client = await connections.OpenAsync(draft with { GameRoot = NormalisedRoot(draft.GameRoot) }, ct, secret, f => fingerprint = f);
        }
        catch (ServerConnectException e)
        {
            Report(new SetupStep("Log in", false, e.Message));
            return new SetupResult(steps, fingerprint, null, null, null);
        }

        using (client)
        {
            Report(new SetupStep("Log in", true, $"Logged in to {draft.Address}:{draft.Port} as {draft.User}."));
            var (root, host, build) = await Task.Run(() => CheckFiles(new SftpServerFolders(client), NormalisedRoot(draft.GameRoot), Report), ct);
            await CheckApiAsync(draft, root, steps, Report, ct);
            return new SetupResult(steps, fingerprint, root, host, build);
        }
    }

    /// <summary>
    /// The game build from the server's <c>Engine/Binaries</c> folders, or null if there's no readable
    /// <c>*-Shipping.version</c> file.
    /// </summary>
    /// <exception cref="IOException">The folders stopped answering.</exception>
    internal static GameBuild? ReadBuild(IServerFolders folders, string gameRoot)
    {
        foreach (var (folder, _) in BINARY_FOLDERS)
        {
            var binaries = folders.Combine(gameRoot, $"Engine/Binaries/{folder}");
            var versionFiles = folders.FileNames(binaries)
                .Where(n => n.EndsWith("-Shipping.version", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal);
            foreach (var name in versionFiles)
            {
                using var stream = folders.OpenRead(folders.Combine(binaries, name));
                if (GameBuild.FromVersionFile(stream) is { } build)
                {
                    return build;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The folder as SFTP expects it: <c>/</c> separators and no trailing one. OpenSSH for Windows
    /// writes drive paths as <c>/C:/…</c>, so <c>C:\SatisfactoryServer</c> becomes <c>/C:/SatisfactoryServer</c>.
    /// </summary>
    internal static string NormalisedRoot(string entered)
    {
        var path = entered.Trim().Replace('\\', '/');
        if (WindowsDrive().IsMatch(path))
        {
            path = "/" + path;
        }
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private async Task<SetupResult> CheckFtpAsync(ServerConnection draft, string? secret, Action<SetupStep> report,
        List<SetupStep> steps, CancellationToken ct)
    {
        string? fingerprint = null;
        FluentFtpSession session;
        try
        {
            session = await connections.OpenFtpAsync(draft with { GameRoot = NormalisedRoot(draft.GameRoot) }, ct, secret, f => fingerprint = f);
        }
        catch (ServerConnectException e)
        {
            report(new SetupStep("Log in", false, e.Message));
            return new SetupResult(steps, fingerprint, null, null, null);
        }

        using (session)
        {
            report(new SetupStep("Log in", true, $"Logged in to {draft.Address}:{draft.Port} as {draft.User}, over TLS."));
            var (root, host, build) = await Task.Run(() => CheckFiles(new FtpServerFolders(session), NormalisedRoot(draft.GameRoot), report), ct);
            await CheckApiAsync(draft, root, steps, report, ct);
            return new SetupResult(steps, fingerprint, root, host, build);
        }
    }

    private async Task<SetupResult> CheckFolderAsync(ServerConnection draft, IReadOnlyCollection<string> detectedRoots,
        Action<SetupStep> report, List<SetupStep> steps, CancellationToken ct)
    {
        var entered = draft.GameRoot.Trim();
        var folders = new LocalServerFolders();
        if (!Path.IsPathFullyQualified(entered) || !folders.IsDirectory(entered))
        {
            report(new SetupStep("Open the folder", false,
                $"Can't open {entered}. If it's on a network share, check the share is mounted, then choose the folder again."));
            return new SetupResult(steps, null, null, null, null);
        }
        report(new SetupStep("Open the folder", true, $"Opened {entered}."));

        var (root, host, build) = await Task.Run(() => CheckFiles(folders, entered, report), ct);
        if (root is not null && detectedRoots.Any(d => SamePath(d, root)))
        {
            report(new SetupStep("Already listed", false,
                "ADA already lists this server from Steam or Epic. Adding it here too would give it two sets of profiles.", Blocking: false));
        }
        await CheckApiAsync(draft, root, steps, report, ct);
        return new SetupResult(steps, null, root, host, build);
    }

    /// <summary>Asks the server API whether it's running, once every blocking check has passed. Never blocks.</summary>
    private async Task CheckApiAsync(ServerConnection draft, string? root, List<SetupStep> steps, Action<SetupStep> report, CancellationToken ct)
    {
        if (root is null || !steps.All(s => s.Passed || !s.Blocking))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(draft.Address))
        {
            report(new SetupStep("Server API", false,
                "No server address given, so ADA can't tell whether the server is running. Stop it yourself before changing mods.",
                Blocking: false));
            return;
        }
        var running = await api.IsRunningAsync(draft.Address, draft.ApiPort, ct);
        report(new SetupStep("Server API", running, running
            ? "The server API answered, so the server is running. Stop it before changing mods."
            : $"No answer on port {draft.ApiPort}: the server is stopped, or the port isn't reachable from this PC. "
              + "If it can't be reached, stop the server yourself before changing mods.", Blocking: false));
    }

    /// <summary>
    /// Finds the server below <paramref name="entered"/> and checks ADA can manage it, reporting each
    /// step; the transport-independent part of the check. Never throws: a connection that stops
    /// answering is reported as a step.
    /// </summary>
    internal static (string? Root, GameHost? Host, GameBuild? Build) CheckFiles(IServerFolders folders, string entered, Action<SetupStep> report)
    {
        try
        {
            return CheckFilesCore(folders, entered, report);
        }
        catch (IOException e)
        {
            report(new SetupStep("Connection", false, $"The server's files stopped answering: {e.Message}"));
            return (null, null, null);
        }
    }

    private static (string? Root, GameHost? Host, GameBuild? Build) CheckFilesCore(IServerFolders folders, string entered, Action<SetupStep> report)
    {
        if (IsGameInstall(folders, entered))
        {
            report(new SetupStep("Find the game", false, GAME_INSTALL_PROBLEM));
            return (null, null, null);
        }
        var root = FindRoot(folders, entered);
        if (root is null)
        {
            report(new SetupStep("Find the game", false,
                $"No dedicated server was found in {entered}. Choose the folder that contains FactoryServer.sh or FactoryServer.exe."));
            return (null, null, null);
        }
        report(new SetupStep("Find the game", true, root == entered ? $"Found the game in {root}." : $"Found the game in {root}, below the folder entered."));

        var host = ServerBinaryHost(folders, root);
        report(new SetupStep("Dedicated server", host is not null, host switch
        {
            GameHost.Linux => "A Linux dedicated server.",
            GameHost.Windows => "A Windows dedicated server.",
            _ => "There's no server program in Engine/Binaries (FactoryServer-Linux-Shipping or FactoryServer-Win64-Shipping), "
                 + "so this isn't a complete dedicated server install.",
        }));
        if (host is null)
        {
            return (null, null, null);
        }

        var build = ReadBuild(folders, root);
        report(new SetupStep("Game build", build is not null, build is null
            ? "Couldn't read the game build, so ADA can't check which mod versions fit."
            : $"Game {build.GameVersion ?? "?"} · CL {build.Changelist}.", Blocking: false));

        var factoryGame = folders.Combine(root, "FactoryGame");
        var writable = CanWrite(folders, factoryGame);
        report(new SetupStep("Can write", writable is null, writable ?? "ADA can create and delete files in FactoryGame."));
        if (writable is not null)
        {
            return (root, host, build);
        }

        var replace = CanReplaceSafely(folders, factoryGame);
        report(new SetupStep("Safe file replace", replace is null, replace ?? "The server can replace a file in one step."));
        return (root, host, build);
    }

    /// <summary>
    /// The entered folder if it holds <c>FactoryGame</c>; otherwise the nearest server install below it.
    /// Game installs found while searching are skipped, so the search can't settle on one.
    /// </summary>
    private static string? FindRoot(IServerFolders folders, string entered)
    {
        if (folders.IsDirectory(folders.Combine(entered, "FactoryGame")))
        {
            return entered;
        }

        var searched = 0;
        var level = new List<string> { entered };
        for (var depth = 0; depth < MAX_SEARCH_DEPTH && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var folder in level)
            {
                foreach (var child in folders.Folders(folder))
                {
                    if (++searched > MAX_SEARCHED_FOLDERS)
                    {
                        return null;
                    }
                    if (IsGameInstall(folders, child))
                    {
                        continue;
                    }
                    if (IsServerInstall(folders, child))
                    {
                        return child;
                    }
                    next.Add(child);
                }
            }
            level = next;
        }
        return null;
    }

    private static bool IsServerInstall(IServerFolders folders, string folder) =>
        folders.IsDirectory(folders.Combine(folder, "FactoryGame"))
        && (folders.FileSize(folders.Combine(folder, "FactoryServer.sh")) is not null
            || folders.FileSize(folders.Combine(folder, "FactoryServer.exe")) is not null);

    /// <summary>A game client: one of its launchers, or a client program in <c>FactoryGame/Binaries/Win64</c>.</summary>
    private static bool IsGameInstall(IServerFolders folders, string folder) =>
        CLIENT_LAUNCHERS.Any(launcher => folders.FileSize(folders.Combine(folder, launcher)) is not null)
        || folders.FileNames(folders.Combine(folder, "FactoryGame/Binaries/Win64"))
            .Any(n => n.StartsWith("FactoryGame", StringComparison.OrdinalIgnoreCase)
                      && n.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The OS of the server program in <c>Engine/Binaries</c>, which every dedicated server has and a
    /// renamed launcher doesn't provide; null when there's no such program, or it's empty.
    /// </summary>
    private static GameHost? ServerBinaryHost(IServerFolders folders, string root)
    {
        if (folders.FileSize(folders.Combine(root, "Engine/Binaries/Linux/FactoryServer-Linux-Shipping")) is > 0)
        {
            return GameHost.Linux;
        }
        var win64 = folders.Combine(root, "Engine/Binaries/Win64");
        if (folders.FileNames(win64).Any(n => n.StartsWith("FactoryServer-Win64-Shipping", StringComparison.OrdinalIgnoreCase)
                                             && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                             && folders.FileSize(folders.Combine(win64, n)) is > 0))
        {
            return GameHost.Windows;
        }
        return null;
    }

    /// <returns>Null when a file can be created and deleted; otherwise why not.</returns>
    private static string? CanWrite(IServerFolders folders, string factoryGame)
    {
        var probe = folders.Combine(factoryGame, $".ada-write-test-{Guid.NewGuid():N}");
        try
        {
            folders.CreateEmpty(probe);
            folders.DeleteFile(probe);
            return null;
        }
        catch (IOException e)
        {
            TryDelete(folders, probe);
            return $"ADA can't write in {factoryGame}: {e.Message}. ADA needs permission to change the server's files.";
        }
    }

    /// <summary>
    /// Replaces one temporary file with another in one step. Without that, ADA could lose its record of
    /// installed mods if the connection dropped mid-save.
    /// </summary>
    /// <returns>Null when it works; otherwise why the server can't be used.</returns>
    private static string? CanReplaceSafely(IServerFolders folders, string factoryGame)
    {
        var id = Guid.NewGuid().ToString("N");
        var from = folders.Combine(factoryGame, $".ada-replace-test-{id}.new");
        var to = folders.Combine(factoryGame, $".ada-replace-test-{id}");
        try
        {
            folders.CreateEmpty(from);
            folders.CreateEmpty(to);
            folders.Replace(from, to);
            return null;
        }
        catch (IOException)
        {
            return folders.ReplaceUnsupported;
        }
        finally
        {
            TryDelete(folders, from);
            TryDelete(folders, to);
        }
    }

    private static void TryDelete(IServerFolders folders, string path)
    {
        try
        {
            folders.DeleteFile(path);
        }
        catch (IOException)
        {
            // Left for the user; it's an empty file named .ada-…-test.
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z]:/")]
    private static partial Regex WindowsDrive();
}
