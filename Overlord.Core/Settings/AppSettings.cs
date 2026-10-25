namespace Overlord.Core.Settings;

/// <summary>User settings, stored in <c>Config/settings.json</c>.</summary>
/// <param name="KeepDownloadedArchives">
/// Keep <c>.smod</c> files after install, so repair, rollback and profile switching work offline
/// and without downloading again. When off, an archive is deleted once its mod is installed.
/// </param>
/// <param name="DownloadRetries">
/// How many times a failed download is retried after the first attempt (network errors, wrong
/// size, wrong hash). Clamped to <see cref="MinDownloadRetries"/>–<see cref="MaxDownloadRetries"/>.
/// </param>
/// <param name="ModsView">The Mods page's layout (List, Grid or Card), as last chosen; null for the default.</param>
/// <param name="ModsSort">The Mods page's sort, as last chosen; null for the default.</param>
/// <param name="ModsFilter">The Mods page's filter, as last chosen; null for the default.</param>
public sealed record AppSettings(
    bool KeepDownloadedArchives = true,
    int DownloadRetries = AppSettings.DefaultDownloadRetries,
    string? ModsView = null,
    string? ModsSort = null,
    string? ModsFilter = null)
{
    public const int DefaultDownloadRetries = 5;
    public const int MinDownloadRetries = 0;
    public const int MaxDownloadRetries = 8;

    public static AppSettings Default { get; } = new();

    /// <summary>The same settings with any out-of-range values (e.g. from a hand-edited file) pulled back into range.</summary>
    public AppSettings Normalized() => this with
    {
        DownloadRetries = Math.Clamp(DownloadRetries, MinDownloadRetries, MaxDownloadRetries),
    };
}
