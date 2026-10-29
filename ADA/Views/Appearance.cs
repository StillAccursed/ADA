using ADA.Core.Settings;
using ADA.Infrastructure.Settings;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ADA.Views;

/// <summary>
/// Applies the Appearance settings (interface scale and theme) app-wide. Static for the same reason
/// as <see cref="InterfaceScale"/>: it acts on Application.Current, outside the service provider.
/// </summary>
internal static class Appearance
{
    /// <summary>
    /// Applies every appearance setting to the running app. Unchanged values cost nothing, so this
    /// is safe to call after any settings save. UI thread only.
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        InterfaceScale.Apply(settings.InterfaceScale);
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = ToVariant(settings.Theme);
        }
    }

    /// <summary>
    /// Re-applies appearance whenever settings are saved. Changed is raised on the saving thread,
    /// so the work is posted to the UI thread.
    /// </summary>
    public static void Follow(SettingsStore settings)
    {
        settings.Changed += (_, _) => Dispatcher.UIThread.Post(() => Apply(settings.Current));
    }

    // ThemeVariant.Default follows the OS appearance, including changes while ADA is running.
    private static ThemeVariant ToVariant(string theme) => theme switch
    {
        AppSettings.THEME_LIGHT => ThemeVariant.Light,
        AppSettings.THEME_DARK => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}
