using ADA.Infrastructure.Installing;
using ADA.Infrastructure.Profiles;
using Microsoft.Extensions.Logging;

namespace ADA.ViewModels;

internal enum ModOperationKind
{
    Install,
    Uninstall,
}

/// <summary>Where one mod stands in the operation queue, for its list row and page.</summary>
internal readonly record struct ModOperationStatus(bool IsQueueBusy, bool? PendingUninstall, int Position, bool IsRunning);

/// <summary>
/// Runs mod changes one at a time, in click order, across every game. Each request is planned
/// against the disk only when it reaches the head, so it accounts for every earlier change.
/// A game start pauses the queue, keeping the current request at the head; <see cref="Resume"/>
/// continues once the game closes. Continuations return to the caller's context, so events are
/// raised on the UI thread when requests are submitted from it.
/// </summary>
internal sealed class ModOperationQueue
{
    private readonly ModResolutionService _resolution;
    private readonly GameProfiles _profiles;
    private readonly DashboardViewModel _dashboard;
    private readonly Func<bool> _isGameRunning;
    private readonly Func<GameInstallViewModel, Task> _refreshAfter;
    private readonly ILogger _log;

    // Entries include the active operation, so duplicate clicks cannot submit it again.
    private readonly Lock _lock = new();
    private readonly Queue<ModOperation> _operations = new();
    private readonly List<string> _outcomes = [];
    private ModOperation? _active;
    private bool _processing;

    /// <summary>Raised when requests are added, started or finished; row and command state should be reapplied.</summary>
    internal event EventHandler? StateChanged;

    /// <summary>Raised with the active request's progress, or null once the worker stops.</summary>
    internal event EventHandler<string?>? ProgressChanged;

    /// <summary>Raised when the worker starts (true) or stops (false).</summary>
    internal event EventHandler<bool>? BusyChanged;

    /// <summary>Raised with every outcome of the current run, one per line, or null when a new run clears them.</summary>
    internal event EventHandler<string?>? OutcomesChanged;

    /// <summary>Raised when the install engine saw the game start before the process watcher did.</summary>
    internal event EventHandler? GameStartDetected;

    /// <summary>Requests waiting behind the active one.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _operations.Count - (_active is null ? 0 : 1);
            }
        }
    }

    /// <summary>Completes when the current worker drains the queue or pauses for the game; for tests.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <param name="isGameRunning">Checked before each request starts and again after it is planned.</param>
    /// <param name="refreshAfter">Refreshes the page for a game after each request, whether or not it succeeded.</param>
    internal ModOperationQueue(ModResolutionService resolution, GameProfiles profiles, DashboardViewModel dashboard,
        Func<bool> isGameRunning, Func<GameInstallViewModel, Task> refreshAfter, ILogger log)
    {
        _resolution = resolution;
        _profiles = profiles;
        _dashboard = dashboard;
        _isGameRunning = isGameRunning;
        _refreshAfter = refreshAfter;
        _log = log;
    }

    /// <summary>
    /// Copies request data at the click. A mod already submitted for this game keeps its existing
    /// place and action; overlapping batch entries are not submitted a second time.
    /// </summary>
    /// <returns>Completes once every listed mod's request has run.</returns>
    internal Task EnqueueAsync(GameInstallViewModel game, ModOperationKind kind, IReadOnlyList<string> references, string? version = null)
    {
        var waiting = new List<Task>();
        var clearedOutcomes = false;
        lock (_lock)
        {
            var newReferences = new List<string>();
            foreach (var reference in references.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Find(game, reference) is { } existing)
                {
                    waiting.Add(existing.Completion.Task);
                }
                else
                {
                    newReferences.Add(reference);
                }
            }

            if (newReferences.Count > 0)
            {
                if (_operations.Count == 0)
                {
                    _outcomes.Clear();
                    clearedOutcomes = true;
                }
                var operation = new ModOperation(game, kind, newReferences.ToArray(), version);
                _operations.Enqueue(operation);
                waiting.Add(operation.Completion.Task);
            }
        }
        if (clearedOutcomes)
        {
            OutcomesChanged?.Invoke(this, null);
            _dashboard.OperationNotice = null;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        Resume();
        return Task.WhenAll(waiting);
    }

    /// <summary>Starts at most one worker; does nothing while the game runs or nothing is queued.</summary>
    internal void Resume()
    {
        lock (_lock)
        {
            if (_processing || _operations.Count == 0 || _isGameRunning())
            {
                return;
            }
            _processing = true;
        }
        Pending = ProcessAsync();
    }

    /// <summary>The mod is waiting or running for this game.</summary>
    internal bool Contains(GameInstallViewModel game, string reference) => Find(game, reference) is not null;

    internal bool IsActiveFor(GameInstallViewModel game, string reference)
    {
        lock (_lock)
        {
            return _active is { } active && active.Game.Install.GamePath == game.Install.GamePath
                && active.References.Contains(reference, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal bool HasOperationsFor(GameInstallViewModel game)
    {
        lock (_lock)
        {
            return _operations.Any(o => o.Game.Install.GamePath == game.Install.GamePath);
        }
    }

    internal ModOperationStatus StatusFor(GameInstallViewModel? game, string reference)
    {
        lock (_lock)
        {
            var operations = _operations.ToArray();
            var operation = game is null ? null : Find(game, reference);
            return new ModOperationStatus(
                operations.Length > 0,
                operation is null ? null : operation.Kind == ModOperationKind.Uninstall,
                operation is null ? 0 : Array.IndexOf(operations, operation) + 1,
                operation is not null && ReferenceEquals(operation, _active));
        }
    }

    /// <summary>
    /// Processes requests in order, recording failures without dropping later work. A late game
    /// start keeps the current request at the head, to be planned afresh after the game closes.
    /// </summary>
    private async Task ProcessAsync()
    {
        ModOperation? finishing = null;
        try
        {
            while (true)
            {
                ModOperation operation;
                lock (_lock)
                {
                    if (_isGameRunning() || !_operations.TryPeek(out var next))
                    {
                        break;
                    }
                    operation = next;
                    _active = operation;
                }
                BusyChanged?.Invoke(this, true);
                StateChanged?.Invoke(this, EventArgs.Empty);

                try
                {
                    RecordOutcome(await PlanAndApplyAsync(operation));
                }
                catch (GameRunningException)
                {
                    // The install engine can see a start before the process watcher. Publish it
                    // there too, so even a short game session produces the later close notification.
                    _dashboard.IsGameRunning = true;
                    foreach (var game in _dashboard.Installs)
                    {
                        game.IsGameRunning = true;
                    }
                    GameStartDetected?.Invoke(this, EventArgs.Empty);
                    break;
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Changing mods in {Game} failed", operation.Game.Install.GamePath);
                    _dashboard.ReportOperationProblem($"{operation.Game.Install.DisplayName}: {e.Message}");
                    RecordOutcome($"Couldn't {operation.Kind.ToString().ToLowerInvariant()} {string.Join(", ", operation.References)}: {e.Message}");
                }

                try
                {
                    await _refreshAfter(operation.Game);
                }
                catch (Exception e)
                {
                    _log.LogError(e, "Refreshing mods in {Game} failed", operation.Game.Install.GamePath);
                    _dashboard.ReportOperationProblem($"Couldn't refresh {operation.Game.Install.DisplayName}: {e.Message}");
                    RecordOutcome($"Couldn't refresh the displayed state: {e.Message}");
                }

                lock (_lock)
                {
                    _operations.Dequeue();
                    _active = null;
                    finishing = operation;
                }
                StateChanged?.Invoke(this, EventArgs.Empty);

                lock (_lock)
                {
                    if (_operations.Count == 0 || _isGameRunning())
                    {
                        break;
                    }
                }
                finishing.Completion.TrySetResult();
                finishing = null;
            }
        }
        finally
        {
            lock (_lock)
            {
                _active = null;
            }
            ProgressChanged?.Invoke(this, null);
            BusyChanged?.Invoke(this, false);
            StateChanged?.Invoke(this, EventArgs.Empty);
            lock (_lock)
            {
                _processing = false;
            }
            finishing?.Completion.TrySetResult();
            Resume();
        }
    }

    /// <summary>Plans against the current disk state only when this request reaches the head.</summary>
    private async Task<string> PlanAndApplyAsync(ModOperation operation)
    {
        var game = operation.Game.Install;
        ProgressChanged?.Invoke(this, $"Working out what needs to change for {string.Join(", ", operation.References)}…");
        var plan = operation.Kind == ModOperationKind.Uninstall
            ? await _resolution.PlanUninstallAsync(game, operation.References[0])
            : operation.Version is { } version
                ? await _resolution.PlanInstallVersionAsync(game, operation.References[0], version)
                : await _resolution.PlanInstallAsync(game, operation.References);

        if (_isGameRunning())
        {
            throw new GameRunningException();
        }
        if (!plan.Succeeded)
        {
            _dashboard.ReportOperationProblem($"{game.DisplayName}: {string.Join(" ", plan.Problems)}");
            return string.Join("\n", plan.Problems);
        }
        if (plan.ChangesNothing)
        {
            return "Already in the requested state; nothing to change.";
        }

        ProgressChanged?.Invoke(this, "Applying changes…");
        var progress = new Progress<ModInstallProgress>(p =>
        {
            if (ReferenceEquals(_active, operation))
            {
                ProgressChanged?.Invoke(this, PlanText.Progress(p));
            }
        });
        var result = await _resolution.ApplyAsync(game, plan, progress);
        var outcome = Describe(result);
        try
        {
            await _profiles.RecordInstalledAsync(game);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Updating the profile for {Game} failed", game.GamePath);
            outcome += $" Couldn't update the profile: {e.Message}";
        }
        return outcome;
    }

    /// <summary>Keeps every outcome in this run so the next request cannot hide an earlier failure.</summary>
    private void RecordOutcome(string outcome)
    {
        _outcomes.Add(outcome);
        OutcomesChanged?.Invoke(this, string.Join("\n", _outcomes));
    }

    private ModOperation? Find(GameInstallViewModel game, string reference)
    {
        lock (_lock)
        {
            return _operations.FirstOrDefault(operation => operation.Game.Install.GamePath == game.Install.GamePath
                && operation.References.Contains(reference, StringComparer.OrdinalIgnoreCase));
        }
    }

    private static string Describe(TransactionResult result)
    {
        var parts = new List<string>();
        if (result.Installed.Count > 0)
        {
            parts.Add($"Installed {string.Join(", ", result.Installed.Select(m => $"{m.Reference} {m.Version}"))}.");
        }

        if (result.Uninstalled.Count > 0)
        {
            parts.Add($"Removed {string.Join(", ", result.Uninstalled)}.");
        }

        return string.Join(" ", parts);
    }

    /// <summary>Immutable request data and a completion signal shared by duplicate submissions.</summary>
    private sealed record ModOperation(GameInstallViewModel Game, ModOperationKind Kind, IReadOnlyList<string> References, string? Version)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
