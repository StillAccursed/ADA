using ADA.Core.Activity;
using ADA.Core.Games;

namespace ADA.Infrastructure.Activity;

/// <summary>How an operation that finished should be recorded.</summary>
/// <param name="Outcome">Succeeded, or Failed for an operation that reports failure in its result rather than throwing (a repair).</param>
/// <param name="Error">For a failure reported in the result.</param>
public sealed record ActivityDescription(
    string Summary,
    IReadOnlyList<string>? Details = null,
    ActivityOutcome Outcome = ActivityOutcome.Succeeded,
    string? Error = null);

/// <summary>Runs an operation and records how it ended.</summary>
public static class ActivityRecording
{
    /// <summary>
    /// Runs <paramref name="work"/>, then records its outcome: <paramref name="describe"/>'s entry when it returns, or
    /// <paramref name="failedSummary"/> with the exception's message when it throws (then rethrows), or a cancellation.
    /// With a null <paramref name="log"/> it only runs the work, so services built without one (in tests) behave as before.
    /// </summary>
    /// <param name="describe">The entry for a result; null records nothing (e.g. a scheduled backup that succeeded).</param>
    /// <param name="failedSummary">Ends in " failed"; a cancellation records it with " cancelled" instead.</param>
    /// <param name="failedDetails">Shown with a failure, e.g. the changes that were attempted.</param>
    /// <param name="isNotAFailure">Exceptions to rethrow without recording, e.g. "nothing to back up" for a scheduled backup.</param>
    public static async Task<T> RecordAsync<T>(this ActivityLog? log, ActivityArea area, string action, GameInstall? install,
        Func<Task<T>> work, Func<T, ActivityDescription?> describe, string failedSummary,
        IReadOnlyList<string>? failedDetails = null, Func<Exception, bool>? isNotAFailure = null)
    {
        if (log is null)
        {
            return await work();
        }

        T result;
        try
        {
            result = await work();
        }
        catch (OperationCanceledException)
        {
            log.Record(area, action, ActivityOutcome.Cancelled, failedSummary.Replace(" failed", " cancelled", StringComparison.Ordinal),
                install, failedDetails);
            throw;
        }
        catch (Exception e) when (isNotAFailure?.Invoke(e) != true)
        {
            log.Record(area, action, ActivityOutcome.Failed, failedSummary, install, failedDetails, e.Message);
            throw;
        }

        if (describe(result) is { } entry)
        {
            log.Record(area, action, entry.Outcome, entry.Summary, install, entry.Details, entry.Error);
        }
        return result;
    }

    /// <summary><see cref="RecordAsync{T}"/> for work with no result.</summary>
    public static Task RecordAsync(this ActivityLog? log, ActivityArea area, string action, GameInstall? install,
        Func<Task> work, ActivityDescription success, string failedSummary, IReadOnlyList<string>? failedDetails = null) =>
        log.RecordAsync(area, action, install, async () =>
        {
            await work();
            return true;
        }, _ => success, failedSummary, failedDetails);
}
