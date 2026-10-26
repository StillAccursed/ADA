# ADA development proposal

Proposal date: 25 October 2026.

Implementation update: the Fix 1 adjustment has subsequently been approved and applied. See [Transaction-Cleanup.md](Transaction-Cleanup.md). The original proposal below is retained for review; the remainder of Batch 1 and the other batches have not been approved or implemented.

**Recommendation:** strengthen transaction safety and plan validation first, then complete Profiles.

This document records the original review proposal. The code is further ahead than parts of `Progress.md`, but several foundations need attention before profile switching adds more complex changes. Release packaging is excluded.

No implementation is approved by this document. The review made no changes. It covered all five projects: **111 C# files and 9 XAML files**, including tests, plus the specifications and build configuration. This was a static review; no build, tests, application launch or game installation changes were performed. The “297 tests passing” statement remains historical evidence from the progress file.

At review time, the checkout was clean at commit `a03b08b`; the progress file referenced an older commit.

For a fuller explanation of the existing behaviour and undesirable outcomes, see [Fix-Behaviour-Review.md](Fix-Behaviour-Review.md).

## Open items reconciled against the implementation

| Item | Current code | Proposal |
| --- | --- | --- |
| Logos in the mod list | Implemented in List, Grid and Card layouts | Mark implemented; verify visually |
| Updating from mod details | Exact-version selection and switching already work; “Latest compatible” does not offer an update for an installed requested mod | Finish the default Update action |
| Markdown descriptions | Still converted to plain text; gallery images are separate | Add formatted rendering later |
| Profiles page | Placeholder; storage and export services exist | Main feature after the safety work |
| Adopted mods absent from ficsit.app | Still block resolution | Support retaining local versions safely |
| Switching after adopting found mods | Changes the profile assignment while retaining existing managed mods | Route through real profile switching |
| Window icon | Avalonia placeholder | Separate branding task |
| Windows/Linux closing, Proton detection, server build-file location | Still require runtime evidence | Dedicated validation batch |
| `.smmprofile` unknowns | Raw document round-tripping preserves unknown fields; full import semantics are unfinished | Define and test before exposing import |
| Specifications in `.claude` | Ignored and absent from the current tracked-file inventory | Move maintained specifications into tracked Docs |
| Activity page | Also a placeholder, although omitted from Open items | Record explicitly in the roadmap |
| Servers | Placeholder | Keep as Phase 3 |

## Findings that change the implementation order

### 1. Cleanup failures can trigger rollback after rollback data has been deleted

`Apply` calls `Finish` inside its rollback-protected block. `Finish` deletes the transaction folder—including old mod versions—before deleting the journal. If journal deletion fails, the catch attempts rollback with the old files already gone. That can leave the installed record inconsistent with the folders.

This needs a durable committed state before cleanup. Once committed, cleanup failures should leave recoverable housekeeping work rather than trigger rollback.

**Source:** [ModInstaller.Apply and Finish](../../ADA.Infrastructure/Installing/ModInstaller.cs).

### 2. Crash recovery preserves individual mods, rather than the complete transaction

Recovery completes verified installs and all uninstalls, then discards unfinished installs. The tests explicitly expect this behaviour. A crash during a profile switch could therefore complete removals without installing the complete replacement profile.

The proposed policy is that an interrupted, uncommitted transaction restores the complete previous state; a committed transaction only finishes cleanup. This deliberately strengthens the current recovery contract.

**Sources:** [ModInstaller.RecoverLocked](../../ADA.Infrastructure/Installing/ModInstaller.cs), [CrashSafetyTests](../../ADA.Tests/Installing/CrashSafetyTests.cs).

### 3. Plans are not bound to their target or the state they were calculated from

`ModChangePlan` contains changes but no game identity or expected installed state. The Mods page captures a game while planning, then reads the current selection when confirming. Changing selection clears an existing plan, but an older asynchronous planning operation can subsequently publish its result.

The installer lock protects folder changes, but requested-mod writes occur outside that lock. A plan should be validated against its original game and installation state under the same operation boundary.

**Sources:** [ModsViewModel.PlanAsync and ConfirmAsync](../ViewModels/ModsViewModel.cs), [ModResolutionService.ApplyAsync](../../ADA.Infrastructure/Installing/ModResolutionService.cs).

### 4. Saved profiles lack information needed for reliable switching

`ModProfile` stores exact versions only. It cannot preserve requested-versus-dependency status, disabled selections or imported version ranges. Both games also initially use the same `Default` profile name, and `RecordInstalledAsync` rewrites that shared profile from whichever game changed.

Profiles need an explicit ownership model before adding switching controls.

**Sources:** [ModProfile](../../ADA.Core/Profiles/ModProfile.cs), [GameProfiles.RecordInstalledAsync](../../ADA.Infrastructure/Profiles/GameProfiles.cs), [ProfileStore](../../ADA.Infrastructure/Profiles/ProfileStore.cs).

### 5. Update downgrade prevention is a preference rather than a constraint

`PlanUpdateAsync` detects downgrades and resolves again with the installed versions preferred. The resolver can still reject those preferences to satisfy another dependency and choose an older version.

Update should enforce version floors; explicit version switching can continue to allow a reviewed downgrade.

**Source:** [ModResolutionService.PlanUpdateAsync](../../ADA.Infrastructure/Installing/ModResolutionService.cs).

## Proposed implementation sequence

Each batch should be reviewed separately.

### Batch 1: make transactions recover as a complete operation

Change `ModInstaller`, the journal models in `InstallRecords`, and their service integration to:

- Record explicit applying, rolling-back and committed phases.
- Retain old folders until the committed state is durable.
- Restore the complete previous transaction after an interruption before commitment.
- Retry cleanup after commitment without attempting rollback.
- Include requested-mod intent in recovery.
- Guard recovery reached through adoption as well as normal installation; `Adopt` currently invokes recovery despite being documented as safe while the game runs.
- Read existing journals explicitly so changing the format cannot misinterpret interrupted work.

The central proposed journal shape would be:

```csharp
/// <summary>
/// Determines whether recovery restores the previous installation
/// or completes cleanup for an installation already committed.
/// </summary>
internal enum TransactionPhase
{
    Applying,
    RollingBack,
    Committed,
}

/// <summary>
/// Persists folder changes and user intent together so recovery
/// restores a consistent installation and requested-mod set.
/// </summary>
/// <param name="RequestedBefore">
/// Null when no requested-mod record existed before the transaction.
/// </param>
/// <param name="RequestedAfter">
/// The requested-mod set belonging to the completed transaction.
/// </param>
internal sealed record Journal(
    string TransactionId,
    DateTimeOffset StartedAt,
    TransactionPhase Phase,
    List<JournalStep> Steps,
    IReadOnlyList<string>? RequestedBefore,
    IReadOnlyList<string> RequestedAfter);
```

`JournalStep` would continue describing each mod's old and new locations and records. The significant change is that recovery follows the transaction phase rather than independently deciding which mods to retain.

**Validation:** extend the existing fault-injection tests to assert the entire previous or completed installation, including requested mods. Cover cleanup failures, interrupted removals, repeated recovery and legacy journals.

### Batch 2: bind plans to their targets and tighten resolution

Change `ModChangePlan`, `ModResolutionService`, `ModInstallService` and the consuming view models to:

- Bind each plan to its game, build, platform and expected installed/requested state.
- Reject stale asynchronous results and stale confirmations.
- Coordinate installation, recovery, adoption and intent writes per game.
- Enforce the no-downgrade rule for updates.
- Represent an adopted local version as a fixed installed candidate using its descriptor's compatibility and dependency information. It must not become a fabricated downloadable catalogue version.
- Check archive identity and version against the planned mod before installation.

This batch should also distinguish “mods changed, profile save failed” from “nothing changed.” Some existing error paths can currently report the latter after a successful installation.

**Validation:** cover delayed plans, changing targets, intervening operations, dependency-driven downgrades and installations containing private mods.

### Batch 3: implement Profiles with a complete preview-and-apply flow

Add `ProfilesViewModel` and `ProfilesView`, backed by a profile planning service rather than placing resolution and installation logic in the page.

The first version should provide:

- Create an empty profile or capture the selected game's managed mods.
- List profiles, their contents and game assignments.
- Preview switching: additions, version changes, removals and blockers.
- Apply through the strengthened transaction boundary.
- Import `.olp` and supported `.smmprofile` files with exact or re-resolved versions.
- Export `.olp`, `.smmprofile` or both.
- Preserve requested mods, disabled selections and imported metadata in internal profile storage while retaining the simple `.olp` exchange format.

The proposed ownership model separates a reusable saved profile from each game's resolved installation state. Changes in one game should not silently rewrite another game's saved configuration.

The found-mod prompt should use this same switching flow. Unsupported `.smmprofile` versions or ambiguous disabled-mod cases should produce an explicit import result rather than silently lose information.

Export also needs attention: `AtomicFile.WriteAll` stages both files but publishes them through separate renames. Failure during the second rename can leave only the first changed. The paired export should recover from publication failure and describe its guarantees accurately.

**Source for paired export:** [AtomicFile.WriteAll](../../ADA.Infrastructure/IO/AtomicFile.cs).

### Batch 4: finish the existing Mods experience and startup resilience

Keep this focused:

- Offer Update for an installed mod under “Latest compatible,” reusing single-mod update planning.
- Preserve explicit version switching.
- Refresh dependency rows and download size when the selected version changes; they currently describe an automatically chosen version.
- Add formatted descriptions as a separate presentation change.
- Handle catalogue corruption during initial service construction. The existing error-handling test damages the database after construction; a corrupt database at startup still reaches the application shutdown path.
- Coordinate shared archive access and cache deletion during active operations.
- Review bitmap disposal and cancellation for logos and galleries.

### Batch 5: reconcile documentation and perform platform validation

Update the roadmap and tracked specifications to match actual behaviour, then validate Windows closing, Linux/Proton process detection and closing, and real dedicated-server layouts. Keep those results distinct from compilation and fixture tests.

Branding and Activity can follow as small independent features. FTP/SFTP remains Phase 3, after local profile switching is reliable.

## Recommended first approval scope

**Batch 1 only.** It addresses a concrete failure path and establishes the recovery guarantee that Profiles will depend on.

The supplied development workflow requires proposal approval before implementation. This document records the proposal; it does not authorize application-code changes.
