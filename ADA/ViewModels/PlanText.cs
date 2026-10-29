using ADA.Infrastructure.Installing;

namespace ADA.ViewModels;

/// <summary>One mod with a newer version than a profile saved; its changelog covers the releases in between.</summary>
public sealed record NewerVersion(string Reference, string Saved, string Newer)
{
    public string Text => $"{Reference} {Saved} → {Newer}";
}

/// <summary>A profile switch's changes, grouped for the Switch profile dialog.</summary>
/// <param name="NotInstalled">Profile mods this game has no build of and doesn't need; they aren't changes.</param>
/// <param name="NotInstalledHeader">The heading for <paramref name="NotInstalled"/>, which depends on whether the game is a server.</param>
public sealed record SwitchChanges(IReadOnlyList<string> Install, IReadOnlyList<string> ChangeVersion, IReadOnlyList<string> Remove,
    IReadOnlyList<string> NotInstalled, string NotInstalledHeader)
{
    public bool HasInstall => Install.Count > 0;

    public bool HasChangeVersion => ChangeVersion.Count > 0;

    public bool HasRemove => Remove.Count > 0;

    public bool HasNotInstalled => NotInstalled.Count > 0;

    public bool IsEmpty => !HasInstall && !HasChangeVersion && !HasRemove;
}

/// <summary>How planned changes and their progress read, wherever they are shown.</summary>
internal static class PlanText
{
    /// <param name="remote">Whether the plan is for a server, which names the mods it leaves out as client-only.</param>
    public static IReadOnlyList<string> Lines(ModChangePlan plan, bool remote)
    {
        var lines = new List<string>();
        foreach (var install in plan.Install)
        {
            lines.Add(install.InstalledVersion is { } from
                ? $"Update {install.Reference} {from} → {install.Version}{Why(install)}"
                : $"Install {install.Reference} {install.Version}{Why(install)}");
        }
        lines.AddRange(plan.Uninstall.Select(r => $"Remove {r}"));
        if (plan.Install.Count == 0 && plan.Uninstall.Count == 0)
        {
            lines.Add("Keep it installed even when no other mod needs it");
        }
        if (plan.Skipped.Count > 0)
        {
            lines.Add($"{NotInstalledText(remote)}: {string.Join(", ", plan.Skipped.Select(s => s.Reference))}");
        }

        if (plan.DownloadBytes > 0)
        {
            lines.Add($"Download up to {ByteSize.Format(plan.DownloadBytes)}");
        }

        return lines;
    }

    /// <summary>
    /// A profile switch's changes in three groups. Version changes may go to an older saved
    /// version, so they read "from → to" without calling it an update.
    /// </summary>
    /// <param name="remote">Whether the switch is on a server, for the heading of the mods it leaves out.</param>
    public static SwitchChanges Switch(ModChangePlan plan, bool remote) => new(
        plan.Install.Where(i => !i.IsUpdate).Select(i => $"{i.Reference} {i.Version}{Why(i)}").ToList(),
        plan.Install.Where(i => i.IsUpdate).Select(i => $"{i.Reference} {i.InstalledVersion} → {i.Version}").ToList(),
        plan.Uninstall.ToList(),
        plan.Skipped.Select(s => s.Reference).ToList(),
        $"{NotInstalledText(remote)} ({plan.Skipped.Count})");

    /// <summary>
    /// Mods the newest plan puts at a different version than the saved plan would, for the Switch
    /// profile dialog's "Update to newer versions" option. A mod the saved plan leaves as installed
    /// is compared with its installed version.
    /// </summary>
    public static IReadOnlyList<NewerVersion> Newer(ModChangePlan saved, ModChangePlan newest)
    {
        var savedVersions = saved.Install.ToDictionary(i => i.Reference, i => i.Version, StringComparer.OrdinalIgnoreCase);
        return newest.Install
            .Select(i => (Install: i, From: savedVersions.GetValueOrDefault(i.Reference) ?? i.InstalledVersion))
            .Where(n => n.From is not null && n.From != n.Install.Version)
            .Select(n => new NewerVersion(n.Install.Reference, n.From!, n.Install.Version))
            .ToList();
    }

    public static string Progress(ModInstallProgress p) => p.Stage switch
    {
        InstallStage.Downloading when p.Total is > 0 => $"Downloading {p.Reference}: {100 * p.Done / p.Total.Value}%",
        InstallStage.Downloading => $"Downloading {p.Reference}…",
        InstallStage.Extracting => $"Installing {p.Reference}: {p.Done} of {p.Total} files",
        _ => $"Finishing {p.Reference}…",
    };

    private static string NotInstalledText(bool remote) =>
        remote ? "Client-only, not installed on the server" : "Server-only, not installed in the game";

    /// <summary>" (needed by …)" for a dependency; empty for a mod the user asked for.</summary>
    private static string Why(PlannedInstall install) => install.Mod.RequiredBy.Contains("requested")
        ? ""
        : $" (needed by {string.Join(", ", install.Mod.RequiredBy)})";
}
