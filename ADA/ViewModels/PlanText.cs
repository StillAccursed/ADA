using ADA.Infrastructure.Installing;

namespace ADA.ViewModels;

/// <summary>How planned changes and their progress read, wherever they are shown.</summary>
internal static class PlanText
{
    public static IReadOnlyList<string> Lines(ModChangePlan plan)
    {
        var lines = new List<string>();
        foreach (var install in plan.Install)
        {
            var why = install.Mod.RequiredBy.Contains("requested")
                ? ""
                : $" (needed by {string.Join(", ", install.Mod.RequiredBy)})";
            lines.Add(install.InstalledVersion is { } from
                ? $"Update {install.Reference} {from} → {install.Version}{why}"
                : $"Install {install.Reference} {install.Version}{why}");
        }
        lines.AddRange(plan.Uninstall.Select(r => $"Remove {r}"));
        if (plan.Install.Count == 0 && plan.Uninstall.Count == 0)
            lines.Add("Keep it installed even when no other mod needs it");
        if (plan.DownloadBytes > 0)
            lines.Add($"Download up to {SettingsViewModel.FormatSize(plan.DownloadBytes)}");
        return lines;
    }

    public static string Progress(ModInstallProgress p) => p.Stage switch
    {
        InstallStage.Downloading when p.Total is > 0 => $"Downloading {p.Reference}: {100 * p.Done / p.Total.Value}%",
        InstallStage.Downloading => $"Downloading {p.Reference}…",
        InstallStage.Extracting => $"Installing {p.Reference}: {p.Done} of {p.Total} files",
        _ => $"Finishing {p.Reference}…",
    };
}
