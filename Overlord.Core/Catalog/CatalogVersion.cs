namespace Overlord.Core.Catalog;

public enum VersionStability
{
    Release,
    Beta,
    Alpha,
}

/// <summary>Platform a mod build targets, matching the top-level folders inside a <c>.smod</c>.</summary>
public enum TargetPlatform
{
    Windows,
    WindowsServer,
    LinuxServer,
}

/// <summary>One downloadable build of a version for one platform.</summary>
/// <param name="DownloadPath">Path relative to the API host, e.g. <c>/v1/version/{id}/Windows/download</c>.</param>
/// <param name="Hash">Hash published by ficsit.app (64 hex characters; algorithm still to be confirmed).</param>
public sealed record VersionTarget(TargetPlatform Platform, string DownloadPath, long? Size, string? Hash);

/// <param name="Condition">Semver range, e.g. <c>^3.12.0</c>.</param>
public sealed record VersionDependency(string ModReference, string Condition, bool Optional);

/// <param name="GameVersionRange">Range over the game changelist, e.g. <c>&gt;=513885</c>.</param>
/// <param name="RequiredOnRemote">Whether the other side of a multiplayer session must have this mod too.</param>
public sealed record CatalogVersion(
    string Id,
    string ModReference,
    string Version,
    string GameVersionRange,
    VersionStability Stability,
    bool RequiredOnRemote,
    DateTimeOffset CreatedAt,
    IReadOnlyList<VersionTarget> Targets,
    IReadOnlyList<VersionDependency> Dependencies)
{
    public VersionTarget? TargetFor(TargetPlatform platform) => Targets.FirstOrDefault(t => t.Platform == platform);
}
