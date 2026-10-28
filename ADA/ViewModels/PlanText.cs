using ADA.Infrastructure.Installing;

namespace ADA.ViewModels;

/// <summary>One mod with a newer version than a profile saved; its changelog covers the releases in between.</summary>
public sealed record NewerVersion(string Reference, string Saved, string Newer)
{
    public string Text => $"{Reference} {Saved} → {Newer}";
}

/// <summary>A profile switch's changes, grouped for the Switch profile dialog.</summary>
public sealed record SwitchChanges(IReadOnlyList<string> Install, IReadOnlyList<string> ChangeVersion, IReadOnlyList<string> Remove)
{
    public bool HasInstall => Install.Count > 0;

    public bool HasChangeVersion => ChangeVersion.Count > 0;

    public bool HasRemove => Remove.Count > 0;

    public bool IsEmpty => !HasInstall && !HasChangeVersion && !HasRemove;
}

/// <summary>How planned changes and their progress read, wherever they are shown.</summary>
internal static class PlanText
{
    public static IReadOnlyList<string> Lines(ModChangePlan plan)
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
    public static SwitchChanges Switch(ModChangePlan plan) => new(
        plan.Install.Where(i => !i.IsUpdate).Select(i => $"{i.Reference} {i.Version}{Why(i)}").ToList(),
        plan.Install.Where(i => i.IsUpdate).Select(i => $"{i.Reference} {i.InstalledVersion} → {i.Version}").ToList(),
        plan.Uninstall.ToList());

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

    /// <summary>" (needed by …)" for a dependency; empty for a mod the user asked for.</summary>
    private static string Why(PlannedInstall install) => install.Mod.RequiredBy.Contains("requested")
        ? ""
        : $" (needed by {string.Join(", ", install.Mod.RequiredBy)})";
}
