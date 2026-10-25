using System.Diagnostics;

namespace Overlord.Platform.Detection;

public sealed record GameProcess(int Id, string Name);

/// <summary>Finds and closes Satisfactory (client or dedicated server) on this machine.</summary>
public static class GameProcesses
{
    // Client: FactoryGameSteam / FactoryGameEGS and their -Win64-Shipping children.
    // Server: FactoryServer and FactoryServer-*-Shipping.
    private static readonly string[] Prefixes = ["FactoryGame", "FactoryServer"];

    public static bool IsAnyRunning() => FindRunning().Count > 0;

    public static IReadOnlyList<GameProcess> FindRunning() =>
        OperatingSystem.IsWindows() ? FindNative() : FindWithPs() ?? FindNative();

    /// <summary>
    /// True for a process name or path that belongs to the game, in any of the forms the OS reports:
    /// <c>FactoryGameEGS-Win64-Shipping</c> (Windows), <c>Y:\Games\Satisfactory\FactoryGameEGS.exe</c>
    /// (Wine under CrossOver or Proton), or a truncated name such as <c>FactoryGameStea</c> (Linux).
    /// </summary>
    public static bool IsGameProcessName(string name)
    {
        var trimmed = name.Trim();
        var lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        var executable = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        return Prefixes.Any(p => executable.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parses one line of <c>ps -axo pid=,comm=</c>. Command names can contain spaces.</summary>
    public static GameProcess? ParsePsLine(string line)
    {
        var trimmed = line.TrimStart();
        var space = trimmed.IndexOf(' ');
        if (space <= 0 || !int.TryParse(trimmed.AsSpan(0, space), out var pid))
            return null;
        var name = trimmed[(space + 1)..].Trim();
        return IsGameProcessName(name) ? new GameProcess(pid, name) : null;
    }

    /// <summary>
    /// Asks the game to quit, waits up to <paramref name="grace"/>, then forces any process still
    /// running to stop. Unsaved progress since the last save or autosave is lost.
    /// </summary>
    /// <returns>True if every process has exited.</returns>
    public static async Task<bool> CloseAsync(IReadOnlyList<GameProcess> processes, TimeSpan grace, CancellationToken ct = default)
    {
        foreach (var process in processes)
            RequestExit(process.Id);

        if (await WaitForExitAsync(processes, grace, ct))
            return true;

        foreach (var process in processes.Where(p => IsAlive(p.Id)))
            ForceExit(process.Id);

        return await WaitForExitAsync(processes, TimeSpan.FromSeconds(5), ct);
    }

    private static IReadOnlyList<GameProcess> FindNative()
    {
        var found = new List<GameProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (IsGameProcessName(process.ProcessName))
                        found.Add(new GameProcess(process.Id, process.ProcessName));
                }
                catch (InvalidOperationException)
                {
                    // The process exited while we were looking.
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Wine processes report the Windows executable as their command name, but .NET's
    /// <see cref="Process.ProcessName"/> on macOS returns a module Wine has loaded instead (seen:
    /// <c>EOSOverlayRenderer-Win64-Shipping.exe</c> for every game process under CrossOver). <c>ps</c>
    /// reports the command name correctly. Only the pid and name columns are read: arguments can
    /// contain login tokens.
    /// </summary>
    /// <returns>Null if <c>ps</c> could not be run.</returns>
    private static IReadOnlyList<GameProcess>? FindWithPs()
    {
        var output = Run("ps", "-axo", "pid=,comm=");
        return output?.Split('\n').Select(ParsePsLine).OfType<GameProcess>().ToList();
    }

    private static void RequestExit(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.CloseMainWindow();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // Already gone.
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Under CrossOver the game is a regular Mac app. Wine ignores SIGTERM (confirmed:
            // the game had to be forced), so send the standard Quit request instead, the same
            // as Quit in its Dock menu; Wine passes it to the game as a Windows session end.
            // Processes that are not apps (the launcher stub) return null here and are skipped.
            var pidText = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Run("osascript", "-l", "JavaScript", "-e",
                $"ObjC.import('AppKit'); var app = $.NSRunningApplication.runningApplicationWithProcessIdentifier({pidText}); app.isNil() ? 'not an app' : app.terminate");
        }
        else
        {
            // Linux: SIGTERM. Whether Proton turns this into a clean shutdown is unconfirmed.
            Run("kill", "-TERM", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static void ForceExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to stop.
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForExitAsync(IReadOnlyList<GameProcess> processes, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!processes.Any(p => IsAlive(p.Id)))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(250, ct);
        }
    }

    private static string? Run(string file, params string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
