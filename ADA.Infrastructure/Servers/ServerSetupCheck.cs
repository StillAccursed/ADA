using System.Text.RegularExpressions;
using ADA.Core.Games;
using ADA.Core.Servers;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

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
/// The Add Server page's Test Connection: logs in, finds the game, and checks ADA can manage mods
/// there safely. Stops at the first blocking failure. Changes nothing on the server except two
/// temporary files, which it removes.
/// </summary>
public sealed partial class ServerSetupCheck(ServerConnections connections, SatisfactoryServerApi api)
{
    private const int MAX_SEARCH_DEPTH = 3;
    private const int MAX_SEARCHED_FOLDERS = 200;

    // Win64 holds Windows server builds; Linux the native Linux server.
    private static readonly (string Folder, GameHost Host)[] BINARY_FOLDERS = [("Linux", GameHost.Linux), ("Win64", GameHost.Windows)];

    /// <param name="draft">The details as entered. An empty host key fingerprint trusts whatever key the server presents.</param>
    /// <param name="secret">The password or key passphrase as typed; null uses the saved one.</param>
    public async Task<SetupResult> RunAsync(ServerConnection draft, string? secret, IProgress<SetupStep>? progress = null,
        CancellationToken ct = default)
    {
        var steps = new List<SetupStep>();
        string? fingerprint = null;
        void Report(SetupStep step)
        {
            steps.Add(step);
            progress?.Report(step);
        }

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
            string? root;
            GameHost? host;
            GameBuild? build;
            try
            {
                (root, host, build) = await Task.Run(() => CheckFiles(client, NormalisedRoot(draft.GameRoot), Report), ct);
            }
            catch (SshException e)
            {
                Report(new SetupStep("Connection", false, $"The server stopped answering: {e.Message}"));
                return new SetupResult(steps, fingerprint, null, null, null);
            }
            if (root is not null && steps.All(s => s.Passed || !s.Blocking))
            {
                var running = await api.IsRunningAsync(draft.Address, draft.ApiPort, ct);
                Report(new SetupStep("Server API", running, running
                    ? "The server API answered, so the server is running. Stop it before changing mods."
                    : $"No answer on port {draft.ApiPort}: the server is stopped, or the port isn't reachable from this PC. "
                      + "If it can't be reached, stop the server yourself before changing mods.", Blocking: false));
            }
            return new SetupResult(steps, fingerprint, root, host, build);
        }
    }

    /// <summary>
    /// The game build in the server's <c>Engine/Binaries</c> folders, or null if there's no readable
    /// <c>*-Shipping.version</c> file.
    /// </summary>
    internal static GameBuild? ReadBuild(SftpClient client, string gameRoot)
    {
        foreach (var (folder, _) in BINARY_FOLDERS)
        {
            var binaries = $"{gameRoot}/Engine/Binaries/{folder}";
            if (Attributes(client, binaries) is not { IsDirectory: true })
            {
                continue;
            }
            var versionFiles = client.ListDirectory(binaries)
                .Where(e => e.IsRegularFile && e.Name.EndsWith("-Shipping.version", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FullName)
                .Order(StringComparer.Ordinal);
            foreach (var file in versionFiles)
            {
                using var stream = client.OpenRead(file);
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

    private static (string? Root, GameHost? Host, GameBuild? Build) CheckFiles(SftpClient client, string entered, Action<SetupStep> report)
    {
        string? root;
        try
        {
            root = FindRoot(client, entered);
        }
        catch (SshException e)
        {
            report(new SetupStep("Find the game", false, $"Couldn't look in {entered}: {e.Message}"));
            return (null, null, null);
        }
        if (root is null)
        {
            report(new SetupStep("Find the game", false,
                $"No dedicated server was found in {entered}. Choose the folder that contains FactoryServer.sh or FactoryServer.exe."));
            return (null, null, null);
        }
        report(new SetupStep("Find the game", true, root == entered ? $"Found the game in {root}." : $"Found the game in {root}, below the folder entered."));

        var host = DetectHost(client, root);
        report(new SetupStep("Server OS", host is not null, host switch
        {
            GameHost.Linux => "A Linux server.",
            GameHost.Windows => "A Windows server.",
            _ => "Couldn't tell whether this is a Linux or Windows server. Choose it below.",
        }, Blocking: false));

        var build = ReadBuild(client, root);
        report(new SetupStep("Game build", build is not null, build is null
            ? "Couldn't read the game build, so ADA can't check which mod versions fit."
            : $"Game {build.GameVersion ?? "?"} · CL {build.Changelist}.", Blocking: false));

        var factoryGame = $"{root}/FactoryGame";
        var writable = CanWrite(client, factoryGame);
        report(new SetupStep("Can write", writable is null, writable ?? "ADA can create and delete files in FactoryGame."));
        if (writable is not null)
        {
            return (root, host, build);
        }

        var replace = CanReplaceSafely(client, factoryGame);
        report(new SetupStep("Safe file replace", replace is null, replace ?? "The server can replace a file in one step."));
        return (root, host, build);
    }

    /// <summary>The entered folder if it holds <c>FactoryGame</c>; otherwise the nearest server install below it.</summary>
    private static string? FindRoot(SftpClient client, string entered)
    {
        if (Attributes(client, $"{entered}/FactoryGame") is { IsDirectory: true })
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
                foreach (var child in Folders(client, folder))
                {
                    if (++searched > MAX_SEARCHED_FOLDERS)
                    {
                        return null;
                    }
                    if (IsServerInstall(client, child))
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

    private static bool IsServerInstall(SftpClient client, string folder) =>
        Attributes(client, $"{folder}/FactoryGame") is { IsDirectory: true }
        && (Attributes(client, $"{folder}/FactoryServer.sh") is { IsRegularFile: true }
            || Attributes(client, $"{folder}/FactoryServer.exe") is { IsRegularFile: true });

    /// <summary>The folders inside <paramref name="folder"/>; none when it can't be listed (e.g. no permission).</summary>
    private static IEnumerable<string> Folders(SftpClient client, string folder)
    {
        try
        {
            return client.ListDirectory(folder)
                .Where(e => e.IsDirectory && e.Name is not ("." or ".."))
                .Select(e => e.FullName)
                .ToList();
        }
        catch (SshException)
        {
            return [];
        }
    }

    private static GameHost? DetectHost(SftpClient client, string root)
    {
        if (Attributes(client, $"{root}/FactoryServer.sh") is { IsRegularFile: true })
        {
            return GameHost.Linux;
        }
        if (Attributes(client, $"{root}/FactoryServer.exe") is { IsRegularFile: true })
        {
            return GameHost.Windows;
        }
        // Some hosts hide the executables; the engine's binaries folder still shows the OS.
        foreach (var (folder, host) in BINARY_FOLDERS)
        {
            if (Attributes(client, $"{root}/Engine/Binaries/{folder}") is { IsDirectory: true })
            {
                return host;
            }
        }
        return null;
    }

    /// <returns>Null when a file can be created and deleted; otherwise why not.</returns>
    private static string? CanWrite(SftpClient client, string factoryGame)
    {
        var probe = $"{factoryGame}/.ada-write-test-{Guid.NewGuid():N}";
        try
        {
            client.Open(probe, FileMode.CreateNew, FileAccess.Write).Dispose();
            client.DeleteFile(probe);
            return null;
        }
        catch (SshException e)
        {
            TryDelete(client, probe);
            return $"ADA can't write in {factoryGame}: {e.Message}. The user needs permission to change the server's files.";
        }
    }

    /// <summary>
    /// Replaces one temporary file with another using <c>posix-rename@openssh.com</c>. Without it, ADA
    /// could lose its record of installed mods if the connection dropped mid-save.
    /// </summary>
    /// <returns>Null when it works; otherwise why the server can't be used.</returns>
    private static string? CanReplaceSafely(SftpClient client, string factoryGame)
    {
        var id = Guid.NewGuid().ToString("N");
        var from = $"{factoryGame}/.ada-replace-test-{id}.new";
        var to = $"{factoryGame}/.ada-replace-test-{id}";
        try
        {
            client.Open(from, FileMode.CreateNew, FileAccess.Write).Dispose();
            client.Open(to, FileMode.CreateNew, FileAccess.Write).Dispose();
            client.RenameFile(from, to, isPosix: true);
            return null;
        }
        catch (Exception e) when (e is SshException or NotSupportedException)
        {
            return "This server's SFTP can't replace a file in one step (posix-rename), so ADA can't keep its records "
                   + "safe there. Servers running OpenSSH support it.";
        }
        finally
        {
            TryDelete(client, from);
            TryDelete(client, to);
        }
    }

    private static void TryDelete(SftpClient client, string path)
    {
        try
        {
            client.DeleteFile(path);
        }
        catch (SshException)
        {
            // Not there, which is the goal.
        }
    }

    private static SftpFileAttributes? Attributes(SftpClient client, string path)
    {
        try
        {
            return client.GetAttributes(path);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
        catch (SftpPermissionDeniedException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Za-z]:/")]
    private static partial Regex WindowsDrive();
}
