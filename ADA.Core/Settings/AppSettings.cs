namespace ADA.Core.Settings;

/// <summary>User settings, stored in <c>Config/settings.json</c>.</summary>
/// <param name="KeepDownloadedArchives">
/// Keep <c>.smod</c> files after install, so repair, rollback and profile switching work offline
/// and without downloading again. When off, an archive is deleted once its mod is installed.
/// </param>
/// <param name="DownloadRetries">
/// How many times a failed download is retried after the first attempt (network errors, wrong
/// size, wrong hash). Clamped to <see cref="MIN_DOWNLOAD_RETRIES"/>–<see cref="MAX_DOWNLOAD_RETRIES"/>.
/// </param>
/// <param name="ModsView">The Mods page's layout (List, Grid or Card), as last chosen; null for the default.</param>
/// <param name="ModsSort">The Mods page's sort, as last chosen; null for the default.</param>
/// <param name="ModsFilter">The Mods page's filter, as last chosen; null for the default.</param>
/// <param name="EndlessScroll">Reveal more matching mods while scrolling instead of using pages.</param>
/// <param name="ModsPageSize">
/// Mods per page when endless scrolling is off; one of <see cref="MODS_PAGE_SIZES"/>, otherwise
/// <see cref="DEFAULT_MODS_PAGE_SIZE"/>.
/// </param>
/// <param name="DashboardLatestModsCount">
/// Recent releases displayed on the dashboard, clamped to
/// <see cref="MIN_DASHBOARD_LATEST_MODS_COUNT"/>–<see cref="MAX_DASHBOARD_LATEST_MODS_COUNT"/>.
/// </param>
/// <param name="InterfaceScale">
/// Size of the whole interface as a percentage; one of <see cref="INTERFACE_SCALES"/>, otherwise
/// <see cref="DEFAULT_INTERFACE_SCALE"/>.
/// </param>
/// <param name="Theme">
/// Colour theme; one of <see cref="THEMES"/>, otherwise <see cref="THEME_SYSTEM"/>, which follows
/// the operating system's appearance.
/// </param>
/// <param name="ServersView">The Servers page's layout (List, Grid or Card), as last chosen; null for the default.</param>
/// <param name="CheckServerUploads">
/// After each file is uploaded to a server, read it back and compare hashes. Off checks sizes only,
/// trusting SSH's own integrity checks for the bytes. Local installs always read back.
/// </param>
public sealed record AppSettings(
    bool KeepDownloadedArchives = true,
    int DownloadRetries = AppSettings.DEFAULT_DOWNLOAD_RETRIES,
    string? ModsView = null,
    string? ModsSort = null,
    string? ModsFilter = null,
    bool EndlessScroll = true,
    int ModsPageSize = AppSettings.DEFAULT_MODS_PAGE_SIZE,
    int DashboardLatestModsCount = AppSettings.DEFAULT_DASHBOARD_LATEST_MODS_COUNT,
    int InterfaceScale = AppSettings.DEFAULT_INTERFACE_SCALE,
    string Theme = AppSettings.THEME_SYSTEM,
    string? ServersView = null,
    bool CheckServerUploads = true)
{
    internal const int DEFAULT_DOWNLOAD_RETRIES = 5;
    public const int MIN_DOWNLOAD_RETRIES = 0;
    public const int MAX_DOWNLOAD_RETRIES = 8;
    internal const int DEFAULT_MODS_PAGE_SIZE = 50;
    internal const int DEFAULT_DASHBOARD_LATEST_MODS_COUNT = 5;
    public const int MIN_DASHBOARD_LATEST_MODS_COUNT = 1;
    public const int MAX_DASHBOARD_LATEST_MODS_COUNT = 20;
    internal const int DEFAULT_INTERFACE_SCALE = 100;
    public const string THEME_SYSTEM = "System";
    public const string THEME_LIGHT = "Light";
    public const string THEME_DARK = "Dark";

    /// <summary>The page sizes the Settings page offers; any other saved value falls back to the default.</summary>
    public static readonly IReadOnlyList<int> MODS_PAGE_SIZES = Array.AsReadOnly([25, 50, 100, 200, 500]);

    /// <summary>
    /// Interface scales the Settings page offers, as percentages. Capped at 150% so the main window's
    /// scaled minimum size (760×480 → 1140×720) still fits a 13" laptop screen.
    /// </summary>
    public static readonly IReadOnlyList<int> INTERFACE_SCALES = Array.AsReadOnly([75, 90, 100, 110, 125, 150]);

    /// <summary>Themes the Settings page offers; any other saved value falls back to System.</summary>
    public static readonly IReadOnlyList<string> THEMES = Array.AsReadOnly([THEME_SYSTEM, THEME_LIGHT, THEME_DARK]);

    public static AppSettings Default { get; } = new();

    /// <summary>The same settings with any out-of-range values (e.g. from a hand-edited file) pulled back into range.</summary>
    public AppSettings Normalized() => this with
    {
        DownloadRetries = Math.Clamp(DownloadRetries, MIN_DOWNLOAD_RETRIES, MAX_DOWNLOAD_RETRIES),
        ModsPageSize = MODS_PAGE_SIZES.Contains(ModsPageSize) ? ModsPageSize : DEFAULT_MODS_PAGE_SIZE,
        DashboardLatestModsCount = Math.Clamp(DashboardLatestModsCount,
            MIN_DASHBOARD_LATEST_MODS_COUNT, MAX_DASHBOARD_LATEST_MODS_COUNT),
        InterfaceScale = INTERFACE_SCALES.Contains(InterfaceScale) ? InterfaceScale : DEFAULT_INTERFACE_SCALE,
        Theme = THEMES.Contains(Theme) ? Theme : THEME_SYSTEM,
    };
}
