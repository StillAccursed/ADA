using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;

namespace ADA.Views;

/// <summary>
/// Applies the user's interface scale to every window. Each window's root sits in a
/// LayoutTransformControl bound to the <see cref="TRANSFORM_KEY"/> resource, so replacing that
/// resource rescales all open windows at once. Window sizes are scaled here so scaled content fits.
/// Static because windows are created with <c>new</c> in code-behind, outside the service provider.
/// </summary>
internal static class InterfaceScale
{
    /// <summary>The application resource every window's LayoutTransformControl binds to.</summary>
    public const string TRANSFORM_KEY = "InterfaceScaleTransform";

    // The sizes each window was designed at; weak, so closed dialogs are still collected.
    private static readonly ConditionalWeakTable<Window, DesignSize> DESIGN_SIZES = new();
    private static int _percent = 100;

    /// <summary>
    /// Rescales every window to <paramref name="percent"/>. Open windows lay out again immediately
    /// and their minimum sizes follow; a size the user dragged the window to is left alone unless
    /// it is now below the minimum. Does nothing if the scale is unchanged. UI thread only.
    /// </summary>
    public static void Apply(int percent)
    {
        if (percent == _percent || Application.Current is not { } app)
        {
            return;
        }
        _percent = percent;
        app.Resources[TRANSFORM_KEY] = new ScaleTransform(Factor, Factor);
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                ScaleMinimum(window);
            }
        }
    }

    /// <summary>
    /// Records the window's designed sizes and scales them, including its opening Width and Height.
    /// Call from the window's constructor after InitializeComponent, before it is shown.
    /// </summary>
    public static void Fit(Window window)
    {
        var design = DESIGN_SIZES.GetValue(window,
            w => new DesignSize(w.Width, w.Height, w.MinWidth, w.MinHeight));
        // NaN (unset, e.g. SizeToContent="Height") stays NaN.
        window.Width = design.Width * Factor;
        window.Height = design.Height * Factor;
        ScaleMinimum(window);
    }

    private static double Factor => _percent / 100.0;

    private static void ScaleMinimum(Window window)
    {
        if (DESIGN_SIZES.TryGetValue(window, out var design))
        {
            window.MinWidth = design.MinWidth * Factor;
            window.MinHeight = design.MinHeight * Factor;
        }
    }

    private sealed record DesignSize(double Width, double Height, double MinWidth, double MinHeight);
}
