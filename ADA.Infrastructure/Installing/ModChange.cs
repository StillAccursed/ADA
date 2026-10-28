using ADA.Core.Catalog;

namespace ADA.Infrastructure.Installing;

/// <summary>A change to apply to one game's mods. An install over an installed mod is an update.</summary>
internal abstract record ModChange
{
    /// <param name="ArchivePath">A verified <c>.smod</c> (see <c>ArchiveDownloader</c>).</param>
    public sealed record Install(string ArchivePath, TargetPlatform Platform, bool IncludeDebugSymbols = true) : ModChange;

    public sealed record Uninstall(string Reference) : ModChange;
}
