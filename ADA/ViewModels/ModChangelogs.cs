using ADA.Core.Catalog;
using ADA.Infrastructure.Catalog;
using Semver;

namespace ADA.ViewModels;

/// <summary>
/// Changelogs between two versions of a mod, for the updates window and the Switch profile dialog:
/// every release after <c>from</c> up to and including <c>to</c>, newest first. Refetches the version
/// list once when the cached one predates changelogs.
/// </summary>
public sealed class ModChangelogs(IModVersionSource versions)
{
    /// <summary>The releases' changelogs as readable text, each headed by its version; never empty.</summary>
    public async Task<string> ForRangeAsync(string reference, string from, string to)
    {
        var between = Between(await versions.GetVersionsAsync(reference), from, to);
        // Version lists cached before changelogs were fetched have none; fetch once more.
        if (between.Count > 0 && between.All(v => v.Changelog is null))
        {
            between = Between(await versions.GetVersionsAsync(reference, refresh: true), from, to);
        }

        var parts = between.Select(v => ModDescription.Readable(v.Changelog) is { Length: > 0 } text
            ? $"{v.Version}\n{text}"
            : $"{v.Version}\nNo changelog given.");
        var changelog = string.Join("\n\n", parts);
        return changelog.Length > 0 ? changelog : "No changelog given.";
    }

    private static List<CatalogVersion> Between(IReadOnlyList<CatalogVersion> versions, string from, string to)
    {
        if (!SemVersion.TryParse(from, SemVersionStyles.Any, out var low) || !SemVersion.TryParse(to, SemVersionStyles.Any, out var high))
        {
            return versions.Where(v => v.Version == to).ToList();
        }

        return versions
            .Select(v => (Version: v, Sem: SemVersion.TryParse(v.Version, SemVersionStyles.Any, out var s) ? s : null))
            .Where(v => v.Sem is not null && SemVersion.ComparePrecedence(v.Sem, low) > 0 && SemVersion.ComparePrecedence(v.Sem, high) <= 0)
            .OrderByDescending(v => v.Sem!, SemVersion.PrecedenceComparer)
            .Select(v => v.Version)
            .ToList();
    }
}
