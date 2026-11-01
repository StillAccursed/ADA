using ADA.Core.Catalog;
using ADA.Infrastructure.Installing;

namespace ADA.Infrastructure.Activity;

/// <summary>Plain-language summaries and detail lines for activity entries.</summary>
public static class ActivityText
{
    /// <summary>"1 mod", "3 mods".</summary>
    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>
    /// What a mod transaction did: "Installed 2 mods, updated 1, removed 1", or "No mods changed". The details list new
    /// mods (<c>AutoTrains 1.3.8</c>), updates (<c>SML 3.11.0 → 3.12.0</c>), reinstalls and removals, each sorted by name.
    /// Versions before the change come from <see cref="TransactionResult.PreviousVersions"/>.
    /// </summary>
    public static ActivityDescription DescribeChange(TransactionResult result)
    {
        var before = result.PreviousVersions ?? new Dictionary<string, string>();
        var details = new List<string>();
        int installed = 0, updated = 0, reinstalled = 0;
        foreach (var mod in result.Installed.OrderBy(m => m.Reference, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.TryGetValue(mod.Reference, out var old))
            {
                installed++;
                details.Add($"{mod.Reference} {mod.Version}");
            }
            else if (!string.Equals(old, mod.Version, StringComparison.OrdinalIgnoreCase))
            {
                updated++;
                details.Add($"{mod.Reference} {old} → {mod.Version}");
            }
            else
            {
                reinstalled++;
                details.Add($"{mod.Reference} {mod.Version} (reinstalled)");
            }
        }
        foreach (var reference in result.Uninstalled.Order(StringComparer.OrdinalIgnoreCase))
        {
            details.Add(before.TryGetValue(reference, out var version) ? $"Removed {reference} {version}" : $"Removed {reference}");
        }

        var parts = new List<string>();
        if (installed > 0)
        {
            parts.Add($"installed {Count(installed, "mod")}");
        }
        if (updated > 0)
        {
            parts.Add(parts.Count == 0 ? $"updated {Count(updated, "mod")}" : $"updated {updated}");
        }
        if (reinstalled > 0)
        {
            parts.Add(parts.Count == 0 ? $"reinstalled {Count(reinstalled, "mod")}" : $"reinstalled {reinstalled}");
        }
        if (result.Uninstalled.Count > 0)
        {
            parts.Add(parts.Count == 0 ? $"removed {Count(result.Uninstalled.Count, "mod")}" : $"removed {result.Uninstalled.Count}");
        }
        return new ActivityDescription(parts.Count == 0 ? "No mods changed" : Capitalise(string.Join(", ", parts)), details);
    }

    /// <summary>The changes a transaction set out to make, shown with a failure: <c>Install AutoTrains 1.3.8</c>, <c>Remove X</c>.</summary>
    public static IReadOnlyList<string> DescribeIntent(IReadOnlyList<CatalogVersion> install, IReadOnlyList<string> uninstall) =>
    [
        .. install.Select(v => $"Install {v.ModReference} {v.Version}"),
        .. uninstall.Select(r => $"Remove {r}"),
    ];

    /// <summary>"Installed 2 mods" → "installed 2 mods", for putting after "Switched to Atria: ".</summary>
    public static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
