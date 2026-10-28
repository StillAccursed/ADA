namespace ADA.Core.Settings;

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
/// <param name="EndlessScroll">Reveal more matching mods while scrolling instead of using pages.</param>
/// <param name="ModsPageSize">Mods per page when endless scrolling is off; unsupported values use 50.</param>
/// <param name="DashboardLatestModsCount">Recent releases displayed on the dashboard, between 1 and 20.</param>
public sealed record AppSettings(
    bool KeepDownloadedArchives = true,
    int DownloadRetries = AppSettings.DefaultDownloadRetries,
    string? ModsView = null,
    string? ModsSort = null,
    string? ModsFilter = null,
    bool EndlessScroll = true,
    int ModsPageSize = AppSettings.DEFAULT_MODS_PAGE_SIZE,
    int DashboardLatestModsCount = AppSettings.DEFAULT_DASHBOARD_LATEST_MODS_COUNT)
{
    public const int DefaultDownloadRetries = 5;
    public const int MinDownloadRetries = 0;
    public const int MaxDownloadRetries = 8;
    public const int DEFAULT_MODS_PAGE_SIZE = 50;
    public const int DEFAULT_DASHBOARD_LATEST_MODS_COUNT = 5;
    public const int MIN_DASHBOARD_LATEST_MODS_COUNT = 1;
    public const int MAX_DASHBOARD_LATEST_MODS_COUNT = 20;

    public static AppSettings Default { get; } = new();

    /// <summary>The same settings with any out-of-range values (e.g. from a hand-edited file) pulled back into range.</summary>
    public AppSettings Normalized() => this with
    {
        DownloadRetries = Math.Clamp(DownloadRetries, MinDownloadRetries, MaxDownloadRetries),
        ModsPageSize = ModsPageSize is 25 or 50 or 100 or 200 or 500
            ? ModsPageSize
            : DEFAULT_MODS_PAGE_SIZE,
        DashboardLatestModsCount = Math.Clamp(DashboardLatestModsCount,
            MIN_DASHBOARD_LATEST_MODS_COUNT, MAX_DASHBOARD_LATEST_MODS_COUNT),
    };
}
