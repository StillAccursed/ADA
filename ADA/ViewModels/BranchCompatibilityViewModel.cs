using ADA.Core.Catalog;

namespace ADA.ViewModels;

/// <summary>Displays a Ficsit branch report as a binary compatibility indicator.</summary>
public sealed class BranchCompatibilityViewModel(CompatibilityState state)
{
    /// <summary>Only a Works report confirms compatibility; missing reports do not.</summary>
    public bool IsCompatible => state == CompatibilityState.Works;

    public string Glyph => IsCompatible ? "✓" : "✕";

    /// <summary>Explains the indicator for tooltips and assistive technology.</summary>
    public string Description => state switch
    {
        CompatibilityState.Works => "Compatible",
        CompatibilityState.Damaged => "Partly broken",
        CompatibilityState.Broken => "Not compatible",
        _ => "Compatibility not reported",
    };
}
