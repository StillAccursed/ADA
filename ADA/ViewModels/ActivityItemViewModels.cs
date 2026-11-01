using System.Globalization;
using ADA.Core.Activity;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADA.ViewModels;

/// <summary>One choice in a filter's ComboBox, shown by <see cref="Label"/>.</summary>
/// <param name="Value">What it filters to; null for "All …".</param>
public sealed record ActivityFilterOption<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>A day's heading and its entries, newest first.</summary>
public sealed record ActivityDayViewModel(string Header, IReadOnlyList<ActivityEntryViewModel> Entries);

/// <summary>One activity entry as a row; expands to its details when it has any.</summary>
public partial class ActivityEntryViewModel : ViewModelBase
{
    /// <param name="zone">The zone its time is shown and grouped in.</param>
    internal ActivityEntryViewModel(ActivityEntry entry, TimeZoneInfo zone)
    {
        Entry = entry;
        Local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(entry.AtUtc, DateTimeKind.Utc), zone);
    }

    public ActivityEntry Entry { get; }

    /// <summary>When it happened, in the page's time zone.</summary>
    internal DateTime Local { get; }

    public string TimeText => Local.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>E.g. "Sunday, 1 November 2026 18:34:12", shown when expanded.</summary>
    public string FullTimeText => Local.ToString("F", CultureInfo.CurrentCulture);

    /// <summary>The area's icon, the same as its page's in the sidebar where it has one.</summary>
    public string Glyph => Entry.Area switch
    {
        ActivityArea.Mods => "▦",
        ActivityArea.Profiles => "☰",
        ActivityArea.Backups => "▣",
        ActivityArea.Saves => "▤",
        ActivityArea.Servers => "⇅",
        ActivityArea.Game => "▶",
        _ => "•",
    };

    public string AreaText => AreaName(Entry.Area);

    public string Summary => Entry.Summary;

    /// <summary>The install and, for a cancellation, "Cancelled", joined with a dot; empty for neither.</summary>
    public string SubtitleText => string.Join(" · ",
        new[] { Entry.Install, Entry.Outcome == ActivityOutcome.Cancelled ? "Cancelled" : null }.OfType<string>());

    public bool HasSubtitle => SubtitleText.Length > 0;

    public bool IsFailed => Entry.Outcome == ActivityOutcome.Failed;

    public IReadOnlyList<string> Details => Entry.Details;

    public string? Error => Entry.Error;

    public bool HasError => Error is not null;

    public bool CanExpand => Details.Count > 0 || HasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpanderGlyph))]
    public partial bool IsExpanded { get; private set; }

    /// <summary>Points right while closed, down while open; nothing for an entry with no details.</summary>
    public string ExpanderGlyph => !CanExpand ? "" : IsExpanded ? "⌄" : "›";

    /// <summary>The filter's and the row's name for an area.</summary>
    internal static string AreaName(ActivityArea area) => area.ToString();

    /// <summary>Whether the summary, install, error or any detail line contains <paramref name="query"/>; an empty query matches all.</summary>
    internal bool Matches(string query) =>
        query.Length == 0
        || Has(Entry.Summary, query)
        || Has(Entry.Install, query)
        || Has(Entry.Error, query)
        || Entry.Details.Any(d => Has(d, query));

    [RelayCommand]
    private void Toggle()
    {
        if (CanExpand)
        {
            IsExpanded = !IsExpanded;
        }
    }

    private static bool Has(string? text, string query) =>
        text is not null && text.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
