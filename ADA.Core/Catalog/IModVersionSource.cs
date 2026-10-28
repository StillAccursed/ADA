namespace ADA.Core.Catalog;

/// <summary>
/// Looks up a mod's versions for the app, from a local cache where possible. Unlike
/// <see cref="IModCatalogSource"/>, which always asks ficsit.app, callers may get a cached list.
/// </summary>
public interface IModVersionSource
{
    /// <summary>All versions of a mod, newest first; empty if the mod doesn't exist.</summary>
    /// <param name="refresh">Fetch from ficsit.app even when a fresh cached list exists.</param>
    Task<IReadOnlyList<CatalogVersion>> GetVersionsAsync(string modReference, bool refresh = false, CancellationToken ct = default);
}
