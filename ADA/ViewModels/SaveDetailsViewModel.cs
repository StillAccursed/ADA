using ADA.Core.Profiles;
using ADA.Core.Saves;
using ADA.Infrastructure.Installing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ADA.ViewModels;

/// <summary>
/// One save's page: its facts, the mods it was saved with compared against the selected install, and its
/// options and session settings. Built from a snapshot; a re-read builds a new one.
/// </summary>
public partial class SaveDetailsViewModel : ViewModelBase
{
    private const string OTHER_SETTINGS_TITLE = "Other settings";

    private const string SKIP_ONBOARDING_KEY = "skiponboarding";

    private readonly IReadOnlyList<SaveModRowViewModel> _mods;

    public SaveGame Save { get; }

    public string Name => Save.Name;

    public string SessionName => Save.Header.SessionName;

    public string PlayTimeText => SaveText.PlayTime(Save.Header.PlayTime);

    public string SavedText => SaveText.When(Save.Header.SavedAtUtc);

    public string SizeText => ByteSize.Format(Save.Size);

    public string FileName => Path.GetFileName(Save.FilePath);

    /// <summary>The folder holding the save, for Show in folder.</summary>
    public string FolderPath => Path.GetDirectoryName(Save.FilePath) ?? Save.FilePath;

    public string BuildText => $"CL {Save.Header.BuildVersion}";

    public string FormatText => $"Header {Save.Header.HeaderVersion} · Save {Save.Header.SaveVersion}";

    public string AdvancedGameSettingsText => SaveText.OnOff(Save.Header.AdvancedGameSettings);

    public string TutorialText => Save.Header.Options.SkippedOnboarding ? "Skipped" : "Played";

    public string VisibilityText => Save.Header.Visibility switch
    {
        SessionVisibility.Private => "Private",
        SessionVisibility.FriendsOnly => "Friends only",
        _ => "Unknown",
    };

    public string PartitionedWorldText => SaveText.OnOff(Save.Header.IsPartitionedWorld);

    public string SaveIdText => Save.Header.SaveIdentifier ?? "Unknown";

    /// <summary>E.g. "286 mods · 3 not installed · 2 different version"; zero counts are left out.</summary>
    public string ModsSummary { get; }

    public bool HasMods => _mods.Count > 0;

    /// <summary>Filters <see cref="ShownMods"/> by name or reference.</summary>
    [ObservableProperty]
    public partial string ModSearch { get; set; } = "";

    /// <summary>The save's mods matching <see cref="ModSearch"/>: missing first, then different versions, then installed.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SaveModRowViewModel> ShownMods { get; private set; }

    /// <summary>
    /// References of recorded mods that aren't installed, for Install missing mods. Empty when the install's
    /// mods couldn't be read, since then nothing is known to be missing.
    /// </summary>
    public IReadOnlyList<string> MissingReferences { get; }

    public bool HasMissingMods => MissingReferences.Count > 0;

    public string InstallMissingText => MissingReferences.Count == 1
        ? "Install 1 missing mod"
        : $"Install {MissingReferences.Count} missing mods";

    /// <summary>Session settings grouped by mod, titled with the mod's name; settings without a mod prefix last.</summary>
    public IReadOnlyList<SessionSettingGroupViewModel> SettingGroups { get; }

    public bool HasSettings => SettingGroups.Count > 0;

    /// <summary>Map options other than session settings and the tutorial flag, which the Game box already shows.</summary>
    public IReadOnlyList<SaveOption> OtherOptions { get; }

    public bool HasOtherOptions => OtherOptions.Count > 0;

    /// <param name="installed">The selected install's mods by reference (ignoring case), or null when they couldn't be read.</param>
    public SaveDetailsViewModel(SaveGame save, IReadOnlyDictionary<string, InstalledMod>? installed)
    {
        Save = save;
        _mods = save.Header.Mods
            .Select(mod => SaveModRowViewModel.Compare(mod, installed))
            .OrderBy(row => row.Status)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ShownMods = _mods;
        MissingReferences = _mods.Where(m => m.Status == SaveModStatus.Missing).Select(m => m.Reference).ToList();
        ModsSummary = Summarize(save.Header, _mods, installed is not null);
        SettingGroups = Group(save.Header.Options.SessionSettings, save.Header.Mods);
        OtherOptions = save.Header.Options.Options
            .Where(o => !o.Key.Equals(SKIP_ONBOARDING_KEY, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// The save's mods as a profile named <paramref name="name"/>, at the versions the save recorded. Mods
    /// recorded without a version are left out, since a profile pins exact versions; a reference recorded
    /// twice is kept once. Nothing is saved here.
    /// </summary>
    public ModProfile ToProfile(string name) => new(name, Save.Header.Mods
        .Where(m => m.Version is not null)
        .DistinctBy(m => m.Reference, StringComparer.OrdinalIgnoreCase)
        .Select(m => new ProfileMod(m.Reference, m.Version!))
        .ToList());

    partial void OnModSearchChanged(string value)
    {
        var search = value.Trim();
        ShownMods = search.Length == 0
            ? _mods
            : _mods.Where(m => m.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                               || m.Reference.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private static string Summarize(SaveHeader header, IReadOnlyList<SaveModRowViewModel> mods, bool knowsInstalled)
    {
        if (mods.Count == 0)
        {
            return header.IsModded ? "Marked as modded, but no mods are recorded" : "No mods";
        }

        var parts = new List<string> { mods.Count == 1 ? "1 mod" : $"{mods.Count} mods" };
        if (knowsInstalled)
        {
            var missing = mods.Count(m => m.Status == SaveModStatus.Missing);
            var different = mods.Count(m => m.Status == SaveModStatus.DifferentVersion);
            if (missing > 0)
            {
                parts.Add($"{missing} not installed");
            }
            if (different > 0)
            {
                parts.Add($"{different} different version");
            }
        }
        return string.Join(" · ", parts);
    }

    private static IReadOnlyList<SessionSettingGroupViewModel> Group(IReadOnlyList<SessionSetting> settings, IReadOnlyList<SaveMod> mods)
    {
        var names = mods.DistinctBy(m => m.Reference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(m => m.Reference, m => m.Name, StringComparer.OrdinalIgnoreCase);
        var byMod = settings
            .Where(s => s.ModReference is not null)
            .GroupBy(s => s.ModReference!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SessionSettingGroupViewModel(names.GetValueOrDefault(g.Key) ?? g.Key,
                g.Select(s => new SessionSettingRowViewModel(s)).ToList()))
            .OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase);
        var other = settings.Where(s => s.ModReference is null).Select(s => new SessionSettingRowViewModel(s)).ToList();
        return other.Count == 0
            ? byMod.ToList()
            : [.. byMod, new SessionSettingGroupViewModel(OTHER_SETTINGS_TITLE, other)];
    }
}
