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
/// <param name="SavesView">The Saves page's layout (List, Grid or Card), as last chosen; null for the default.</param>
/// <param name="BackupFolder">Where backups are kept; null for <c>ADA/Backups</c>.</param>
/// <param name="BackupIntervalHours">
/// Hours between scheduled backups; 0 turns the schedule off. One of <see cref="BACKUP_INTERVALS"/>, otherwise off.
/// </param>
/// <param name="BackupInstall">The game path of the install the schedule backs up; null when none was chosen.</param>
/// <param name="BackupParts">
/// Parts the schedule backs up, as <c>BackupPart</c> names joined by ", "; null for every part the install can have.
/// </param>
/// <param name="KeepAutomaticBackups">
/// How many automatic backups of each kind to keep per install (scheduled, before mod changes, before restores);
/// clamped to <see cref="MIN_KEEP_AUTOMATIC_BACKUPS"/>–<see cref="MAX_KEEP_AUTOMATIC_BACKUPS"/>.
/// </param>
/// <param name="BackupBeforeModChanges">Back up saves before installing, removing or switching mods.</param>
/// <param name="MaxLogFolderMegabytes">
/// The most the logs folder may hold, in MB; one of <see cref="LOG_FOLDER_LIMITS"/>, otherwise
/// <see cref="DEFAULT_MAX_LOG_FOLDER_MEGABYTES"/>.
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
    bool CheckServerUploads = true,
    string? SavesView = null,
    string? BackupFolder = null,
    int BackupIntervalHours = 0,
    string? BackupInstall = null,
    string? BackupParts = null,
    int KeepAutomaticBackups = AppSettings.DEFAULT_KEEP_AUTOMATIC_BACKUPS,
    bool BackupBeforeModChanges = false,
    int MaxLogFolderMegabytes = AppSettings.DEFAULT_MAX_LOG_FOLDER_MEGABYTES)
{
    internal const int DEFAULT_DOWNLOAD_RETRIES = 5;
    public const int MIN_DOWNLOAD_RETRIES = 0;
    public const int MAX_DOWNLOAD_RETRIES = 8;
    internal const int DEFAULT_MODS_PAGE_SIZE = 50;
    internal const int DEFAULT_DASHBOARD_LATEST_MODS_COUNT = 5;
    public const int MIN_DASHBOARD_LATEST_MODS_COUNT = 1;
    public const int MAX_DASHBOARD_LATEST_MODS_COUNT = 20;
    internal const int DEFAULT_INTERFACE_SCALE = 100;
    internal const int DEFAULT_KEEP_AUTOMATIC_BACKUPS = 10;
    public const int MIN_KEEP_AUTOMATIC_BACKUPS = 1;
    public const int MAX_KEEP_AUTOMATIC_BACKUPS = 100;
    public const int DEFAULT_MAX_LOG_FOLDER_MEGABYTES = 2048;
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

    /// <summary>The intervals the Schedule box offers, in hours; 0 is off. Any other saved value turns the schedule off.</summary>
    public static readonly IReadOnlyList<int> BACKUP_INTERVALS = Array.AsReadOnly([0, 1, 3, 6, 12, 24, 168]);

    /// <summary>The logs folder limits the Settings page offers, in MB; any other saved value falls back to the default.</summary>
    public static readonly IReadOnlyList<int> LOG_FOLDER_LIMITS = Array.AsReadOnly([100, 250, 500, 1024, 2048, 5120]);

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
        BackupIntervalHours = BACKUP_INTERVALS.Contains(BackupIntervalHours) ? BackupIntervalHours : 0,
        KeepAutomaticBackups = Math.Clamp(KeepAutomaticBackups, MIN_KEEP_AUTOMATIC_BACKUPS, MAX_KEEP_AUTOMATIC_BACKUPS),
        MaxLogFolderMegabytes = LOG_FOLDER_LIMITS.Contains(MaxLogFolderMegabytes)
            ? MaxLogFolderMegabytes
            : DEFAULT_MAX_LOG_FOLDER_MEGABYTES,
    };
}
