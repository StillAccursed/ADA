using ADA.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

/// <summary>
/// Remembers the Mods page's layout, sort and filter in settings. Saves run off the UI thread;
/// whichever runs last writes the newest choices, so saves can't land out of order. Failures are
/// logged and otherwise ignored: the page still works and opens on the defaults next time.
/// </summary>
internal sealed class ModsPageChoices(SettingsStore settings, ILogger log)
{
    private (string View, string Sort, string Filter) _latest;

    /// <summary>Completes when the most recent save has finished; for tests.</summary>
    internal Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The saved choices. Unknown or missing values are null. A saved Queued filter is dropped,
    /// since the queue lasts a session and would open on an empty list.
    /// </summary>
    internal (ModView? View, ModSort? Sort, ModFilter? Filter) Restore()
    {
        try
        {
            var saved = settings.Current;
            ModView? view = Enum.TryParse<ModView>(saved.ModsView, out var v) ? v : null;
            ModSort? sort = Enum.TryParse<ModSort>(saved.ModsSort, out var s) ? s : null;
            ModFilter? filter = Enum.TryParse<ModFilter>(saved.ModsFilter, out var f) && f != ModFilter.Queued ? f : null;
            return (view, sort, filter);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Couldn't read the Mods page's saved choices");
            return (null, null, null);
        }
    }

    internal void Save(ModView view, ModSort sort, ModFilter filter)
    {
        _latest = (view.ToString(), sort.ToString(), filter.ToString());
        PendingSave = Task.Run(() =>
        {
            try
            {
                settings.Update(s =>
                {
                    var (latestView, latestSort, latestFilter) = _latest;
                    return s with { ModsView = latestView, ModsSort = latestSort, ModsFilter = latestFilter };
                });
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Couldn't save the Mods page's choices");
            }
        });
    }
}
