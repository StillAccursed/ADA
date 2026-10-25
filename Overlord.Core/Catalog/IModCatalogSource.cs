namespace Overlord.Core.Catalog;

public enum CatalogOrder
{
    /// <summary>Oldest first. Stable while paging: new mods are appended at the end.</summary>
    CreatedAscending,

    /// <summary>Most recently released version first; used to pick up new versions cheaply.</summary>
    LatestVersionDescending,
}

/// <summary>Where catalogue data comes from (ficsit.app in production, fakes in tests).</summary>
public interface IModCatalogSource
{
    Task<CatalogPage> GetModsAsync(int offset, int limit, CatalogOrder order, CancellationToken ct = default);

    /// <summary>All versions of a mod, newest first. Empty if the mod does not exist.</summary>
    Task<IReadOnlyList<CatalogVersion>> GetVersionsAsync(string modReference, CancellationToken ct = default);
}

public sealed record CatalogPage(IReadOnlyList<CatalogMod> Mods, int TotalCount);
