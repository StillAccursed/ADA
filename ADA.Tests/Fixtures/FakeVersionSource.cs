using ADA.Core.Catalog;

namespace ADA.Tests.Fixtures;

/// <summary>An <see cref="IModVersionSource"/> backed by a lambda; ignores <c>refresh</c>.</summary>
internal sealed class FakeVersionSource(Func<string, CancellationToken, Task<IReadOnlyList<CatalogVersion>>> getVersions)
    : IModVersionSource
{
    public Task<IReadOnlyList<CatalogVersion>> GetVersionsAsync(string modReference, bool refresh = false,
        CancellationToken ct = default) => getVersions(modReference, ct);
}
