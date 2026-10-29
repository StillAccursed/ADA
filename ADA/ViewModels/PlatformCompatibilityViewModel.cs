using ADA.Core.Catalog;

namespace ADA.ViewModels;

/// <summary>
/// Shows whether a mod's newest release has a build for game clients or for dedicated servers, as
/// ficsit.app lists them. Like the branch indicators, it ignores the selected version and game.
/// A missing build the other side doesn't need (e.g. a graphics mod on a server) shows as neutral.
/// </summary>
public sealed class PlatformCompatibilityViewModel
{
    private readonly LatestVersion? _latest;
    private readonly string _side;
    private readonly string _otherSide;
    private readonly IReadOnlyList<TargetPlatform> _platforms;
    private readonly bool? _requiredOnRemote;

    private PlatformCompatibilityViewModel(IReadOnlyList<LatestVersion>? latest, string side, string otherSide,
        IReadOnlyList<TargetPlatform> platforms, bool? requiredOnRemote)
    {
        _latest = Newest(latest);
        _side = side;
        _otherSide = otherSide;
        _platforms = platforms;
        _requiredOnRemote = requiredOnRemote;
    }

    /// <summary>The newest release has a build for this side.</summary>
    public bool IsCompatible => _latest is not null && _platforms.Any(_latest.Targets.Contains);

    /// <summary>
    /// No build for this side, but the mod doesn't need to be there. Only known once the full
    /// version is loaded; until then a missing build shows as not compatible.
    /// </summary>
    public bool IsNotNeeded => _latest is not null && !IsCompatible && _requiredOnRemote == false;

    public string Glyph => IsCompatible ? "✓" : IsNotNeeded ? "–" : "✕";

    /// <summary>Explains the indicator for tooltips and assistive technology, naming the version it's based on.</summary>
    public string Description
    {
        get
        {
            if (_latest is null)
            {
                return "Builds not known yet";
            }
            if (IsNotNeeded)
            {
                return $"{Capitalised(_otherSide)}-only in {_latest.Version}: {_side}s don't need it";
            }
            if (!IsCompatible)
            {
                return $"No {_side} build in {_latest.Version}";
            }
            return _platforms.Count == 1
                ? $"Has a {_side} build in {_latest.Version}"
                : $"{ServerSystems(_latest.Targets)} in {_latest.Version}";
        }
    }

    /// <summary>The version the indicator describes, so callers can look up its full details.</summary>
    internal string? Version => _latest?.Version;

    /// <summary>Game clients, which always use the Windows build (also under CrossOver and Proton).</summary>
    /// <param name="requiredOnRemote">From the full version once loaded; null until then.</param>
    public static PlatformCompatibilityViewModel ForClient(IReadOnlyList<LatestVersion>? latest, bool? requiredOnRemote = null) =>
        new(latest, "client", "server", [TargetPlatform.Windows], requiredOnRemote);

    /// <summary>Dedicated servers on either OS; the tooltip says which.</summary>
    /// <param name="requiredOnRemote">From the full version once loaded; null until then.</param>
    public static PlatformCompatibilityViewModel ForServer(IReadOnlyList<LatestVersion>? latest, bool? requiredOnRemote = null) =>
        new(latest, "server", "client", [TargetPlatform.WindowsServer, TargetPlatform.LinuxServer], requiredOnRemote);

    /// <summary>The newest release, or the newest beta or alpha when the mod has no release.</summary>
    private static LatestVersion? Newest(IReadOnlyList<LatestVersion>? latest) =>
        (latest ?? []).OrderBy(l => l.Stability).FirstOrDefault();

    private static string Capitalised(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    private static string ServerSystems(IReadOnlyList<TargetPlatform> targets) =>
        (targets.Contains(TargetPlatform.WindowsServer), targets.Contains(TargetPlatform.LinuxServer)) switch
        {
            (true, true) => "Windows and Linux servers",
            (true, false) => "Windows servers only",
            _ => "Linux servers only",
        };
}
