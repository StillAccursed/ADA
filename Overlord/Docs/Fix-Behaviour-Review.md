# Overlord: proposed fixes and current behaviour

Review date: 25 October 2026.

Implementation update: the Fix 1 adjustment has subsequently been approved and applied. See [Transaction-Cleanup.md](Transaction-Cleanup.md) for its current behaviour. The explanations below record the original review; the other fixes remain proposals.

This document explains the previous proposal. It describes the current behaviour, why that behaviour is undesirable, and what each proposed change would achieve. Release packaging is excluded.

The findings come from reading the implementation and existing tests. The failure scenarios below have not been reproduced during this review. No application code has been changed, and no builds, tests or game operations have been run.

Some items are defects; others are intentional limitations, unfinished features or missing runtime evidence. Those distinctions matter when deciding what to approve.

## 1. Transaction cleanup can attempt rollback after deleting the old files

**Type:** correctness defect. **Priority:** first batch.

### Current behaviour

During an update, Overlord moves the old mod folder into its transaction working folder and moves the new folder into the game. Keeping the old folder allows a failure to be undone.

After all changes succeed, `Finish` deletes the transaction working folder and then deletes the journal. Both actions remain inside the block whose exception handler attempts to roll back the transaction.

For example:

1. Mod A is successfully updated from version 1 to version 2.
2. Cleanup deletes the old version 1 backup.
3. Deleting the journal fails with an I/O error.
4. The exception handler attempts to restore version 1, whose backup has already been deleted.

### Why this is undesirable

Rollback no longer has everything it needs. Depending on whether the mod changed location, the files can remain at the new version or become absent while the installed record is restored to the old version.

A cleanup failure can therefore turn a successful installation into an inconsistent installation.

### Proposed behaviour

Persist an explicit committed state before deleting any rollback data. Before commitment, failures restore the previous state. After commitment, failures leave cleanup to be retried; they do not attempt rollback.

**Relevant code:** [ModInstaller.Apply, Finish and RollBack](../../Overlord.Infrastructure/Installing/ModInstaller.cs).

## 2. Crash recovery can leave a transaction partly applied

**Type:** intentional recovery policy that is insufficient for complete profile switching. **Priority:** first batch.

### Current behaviour

Overlord installs and commits mods one at a time. After a crash, recovery completes verified changes, completes uninstalls and discards installs that had not finished verification.

Each individual mod ends up whole, but the complete set of changes can be only partly applied. An existing test specifically expects A to be updated, B to be removed and an unfinished C installation to be discarded.

### Why this is undesirable

Individually complete mods can still form an incomplete or incompatible set. A dependency and its dependent mod may end up at different stages of an update.

This becomes particularly important for profile switching: some old mods could be removed without the complete replacement profile being installed. The outcome would match neither the previous profile nor the requested profile.

### Proposed behaviour

Recover the transaction as a complete operation. An interruption before commitment restores the complete previous installation. An interruption after commitment retains the complete new installation and finishes cleanup.

This changes the existing recovery policy; it is stronger than the current per-mod guarantee. The crash tests must change accordingly.

**Relevant code:** [ModInstaller.RecoverLocked](../../Overlord.Infrastructure/Installing/ModInstaller.cs) and [CrashSafetyTests](../../Overlord.Tests/Installing/CrashSafetyTests.cs).

## 3. Requested-mod intent is saved separately from the installation

**Type:** consistency and concurrency weakness. **Priority:** first and second batches.

### Current behaviour

Overlord distinguishes mods the user requested from dependencies installed on their behalf. This distinction controls which dependencies can later be removed as unneeded.

`ModResolutionService.ApplyAsync` saves additions to `requested.json` before calling the installation service, then saves the final requested set after installation succeeds. These writes sit outside the installer's file lock and journal.

The ordering is deliberate: it reduces the chance that a crash makes a wanted mod look unneeded. It does not make intent and installation one recoverable operation.

### Why this is undesirable

A failed operation can leave intent changed even when folder changes were rolled back. Overlapping operations can also read different snapshots and overwrite one another's requested sets.

For example, a later operation could preserve a newly installed mod's files but lose the record that the user explicitly wanted it. A future uninstall could then treat it as a disposable dependency.

### Proposed behaviour

Record the previous and intended requested sets in the transaction journal, and update them within the same per-game operation boundary as the installation. Recovery must restore or complete both consistently, including preserving the meaning of an originally absent requested record.

**Relevant code:** [ModResolutionService.ApplyAsync](../../Overlord.Infrastructure/Installing/ModResolutionService.cs) and [RequestedModsStore](../../Overlord.Infrastructure/Installing/InstallRecords.cs).

## 4. Adoption can perform recovery while the game is running

**Type:** missing guard on a recovery path. **Priority:** first batch.

### Current behaviour

Adopting mods normally hashes their existing files and records ownership without moving them. That operation is documented as safe while the game runs.

However, `Adopt` first calls `RecoverLocked` if an interrupted transaction exists. That recovery can move or remove game-visible mod folders. This path does not check whether the game is running.

### Why this is undesirable

The user can choose an action described as recording existing mods and indirectly trigger folder changes while the game is using those files.

Normal adoption does not cause this problem; adoption with a pending transaction does.

### Proposed behaviour

Make the game-running guard apply to every recovery entry point. If adoption requires recovery first, defer it until recovery can safely run and explain why it is waiting.

**Relevant code:** [ModInstaller.Adopt](../../Overlord.Infrastructure/Installing/ModInstaller.cs).

## 5. An asynchronous plan can outlive its selected game

**Type:** stale-result defect. **Priority:** second batch.

### Current behaviour

Planning captures the selected game before awaiting resolution. When resolution finishes, it publishes a pending plan without checking that the game selection is still the same.

Changing games clears an existing pending plan, but it does not stop an earlier planning operation from subsequently publishing its result. Confirmation then combines that plan with the game selected at confirmation time.

### Why this is undesirable

A plan calculated for one game can be offered for another game. The two games may have different installed mods, builds or target platforms. Lower-level installation checks can reject some conflicts, but they do not establish that this is the plan the user reviewed for that target.

### Proposed behaviour

Bind the plan to its original game, build and platform. Reject late results after the selection changes, and confirm against the plan's bound target. Selection changes must also invalidate related installed-state and details loads.

**Relevant code:** [ModsViewModel.PlanAsync, ConfirmAsync and OnSelectedGameChanged](../ViewModels/ModsViewModel.cs).

## 6. A reviewed plan can become stale before it is applied

**Type:** missing state validation and coordination. **Priority:** second batch.

### Current behaviour

A plan contains changes but no snapshot identifier for the installed mods or requested set used to calculate it.

The installer prevents simultaneous folder transactions using a file lock. It does not establish that a previously calculated dependency plan still matches the installation when that transaction starts. View models also maintain their own busy flags rather than sharing one per-game operation state.

### Why this is undesirable

Another operation can change the installation after the preview is created. For example, newly installed B may need a dependency that an older pending removal plan considers unneeded.

Serializing folder writes prevents simultaneous renames; it does not make the older plan correct.

### Proposed behaviour

Store the expected installation and requested-state identity in the plan. Validate it under the operation lock before applying changes. If the state changed, ask the user to review a fresh plan. Coordinate installation, adoption, recovery and intent writes through the same per-game boundary.

**Relevant code:** [ModChangePlan and ModResolutionService](../../Overlord.Infrastructure/Installing/ModResolutionService.cs), [ModInstallService](../../Overlord.Infrastructure/Installing/ModInstallService.cs).

## 7. Update planning can still choose a downgrade

**Type:** incomplete enforcement of the update policy. **Priority:** second batch.

### Current behaviour

If an update plan chooses an older installed version, Overlord resolves again with the current version marked as preferred. The resolver can ignore that preference when it does not satisfy other dependencies. The second result is not checked again for downgrades.

### Why this is undesirable

Suppose installed library L is version 2, while a newer version of mod A requires L version 1. Preferring L version 2 does not prevent the resolver from choosing L version 1 to satisfy A.

The user chose Update, but a dependency can move backwards despite the intended no-downgrade policy.

### Proposed behaviour

Treat each installed version as a minimum permitted version during update planning. Find a combination that respects those limits or explain why the update cannot proceed. Keep reviewed downgrades available through explicit version switching.

**Relevant code:** [ModResolutionService.PlanUpdateAsync](../../Overlord.Infrastructure/Installing/ModResolutionService.cs) and [DependencyResolver](../../Overlord.Core/Resolution/DependencyResolver.cs).

## 8. A private adopted mod can block unrelated changes

**Type:** unsupported local-mod case. **Priority:** second batch.

### Current behaviour

Overlord can adopt a mod already on disk without requiring it to exist on ficsit.app. Later planning resolves the requested set using catalogue versions. A requested private mod has no catalogue candidates, so resolution fails.

### Why this is undesirable

Adoption offers to let Overlord manage the installation, but accepting it can make later install, fix, update or uninstall plans fail, including changes involving other mods.

### Proposed behaviour

Allow a validated local installed version to participate as a fixed candidate. Use its descriptor's game compatibility and dependency constraints, retain its files, and clearly distinguish it from a downloadable catalogue version.

Missing or uninterpretable metadata must produce an explicit limitation. Overlord should not invent a downloadable replacement or promise that another machine can reproduce a private mod through an exported profile.

**Relevant code:** [GameProfiles](../../Overlord.Infrastructure/Profiles/GameProfiles.cs), [ModResolutionService](../../Overlord.Infrastructure/Installing/ModResolutionService.cs), [UPluginDescriptor](../../Overlord.Infrastructure/Installing/UPluginDescriptor.cs).

## 9. Installation does not compare archive identity with the planned mod

**Type:** missing consistency check. **Priority:** second batch.

### Current behaviour

Downloads are checked against the size and hash supplied by the API when those values are present. The installer subsequently reads the archive's mod reference and version from its `.uplugin` and uses those values for installation.

The `ModChange.Install` passed to the installer carries the archive path and platform, but not the expected mod reference and version. Consequently, it cannot compare the descriptor with the approved plan.

### Why this is undesirable

A matching download hash establishes that the downloaded file matches the API's advertised bytes. It does not establish that the archive's descriptor names the mod and version the plan expected.

If the published archive is incorrectly labelled, Overlord could install different mod identity or version metadata from the preview.

### Proposed behaviour

Carry the expected identity into archive validation and reject mismatches before changing game folders. Continue verifying download integrity separately.

**Relevant code:** [ModChange.Install and ModInstaller.Plan](../../Overlord.Infrastructure/Installing/ModInstaller.cs), [ModInstallService.ApplyAsync](../../Overlord.Infrastructure/Installing/ModInstallService.cs).

## 10. Profiles cannot yet preserve the complete meaning of a selection

**Type:** data-model limitation and unfinished feature. **Priority:** third batch.

### Current behaviour

An Overlord profile stores a name and exact mod versions. It does not distinguish requested mods from dependencies or retain disabled selections and imported version ranges.

The `.smmprofile` document reader preserves unknown fields during a direct read/write round trip. That does not mean those fields survive conversion to the simpler `ModProfile` model or export of a newly constructed document.

### Why this is undesirable

Two selections with the same installed files can have different meanings. A user may explicitly want library L, while another profile contains L only because A requires it. Losing that distinction changes later dependency removal.

Likewise, silently dropping a disabled selection would make a later export or profile edit lose information the user intended to keep.

### Proposed behaviour

Preserve selection intent and relevant import metadata in internal profile storage, alongside exact resolved versions. Keep the simple `.olp` exchange format, and make any conversion losses explicit. Unsupported lockfile versions should remain rejected until their semantics are known.

**Relevant code:** [ModProfile](../../Overlord.Core/Profiles/ModProfile.cs), [SmmProfileFile](../../Overlord.Infrastructure/Profiles/SmmProfileFile.cs), [ProfileExporter](../../Overlord.Infrastructure/Profiles/ProfileExporter.cs).

## 11. Different games can overwrite the same saved profile

**Type:** ambiguous ownership with an existing overwrite path. **Priority:** third batch.

### Current behaviour

Game-to-profile assignments are stored separately per game path, but profiles themselves are stored globally by name. An unassigned game uses the name `Default`.

After a mod change, `RecordInstalledAsync` replaces that named profile with the selected game's managed installation. Two games using `Default` therefore write the same saved profile.

### Why this is undesirable

Changing mods in game B can replace the saved contents previously captured from game A. Their actual game folders remain separate, but the reusable saved profile no longer reliably represents the user's intended configuration.

### Proposed behaviour

Separate reusable saved profiles from each game's resolved installation state. Saving changes back to a shared profile should be explicit, and an unassigned game's working state should not overwrite another game's working state.

This is a proposed product decision: it changes the current rule that every mod change automatically rewrites the active named profile.

**Relevant code:** [ProfileStore.ActiveName and Save](../../Overlord.Infrastructure/Profiles/ProfileStore.cs), [GameProfiles.RecordInstalledAsync](../../Overlord.Infrastructure/Profiles/GameProfiles.cs).

## 12. Switching after adoption currently merges rather than replaces

**Type:** incomplete switching behaviour. **Priority:** third batch.

### Current behaviour

Choosing to add found mods to a new profile and switch immediately adopts them alongside existing managed mods. The new profile is built from the complete resulting installation, then made active. Previously managed mods remain installed.

The Profiles page itself is still a placeholder.

### Why this is undesirable

The word Switch suggests that the game will match the chosen profile. Currently, the action effectively captures a merged installation under a new name.

### Proposed behaviour

Build one preview of everything required to make the game match the target profile: additions, version changes, removals and blockers. Apply it through the transaction engine and complete the profile assignment through recoverable bookkeeping.

Creating a profile for later use should remain distinct from switching the game to it immediately.

**Relevant code:** [GameProfiles.ImportToNewAsync and AdoptIntoAsync](../../Overlord.Infrastructure/Profiles/GameProfiles.cs), [MainViewModel](../ViewModels/MainViewModel.cs).

## 13. Exporting both formats is not atomic as a pair

**Type:** incomplete multi-file failure handling. **Priority:** third batch.

### Current behaviour

Overlord prepares both temporary files before publishing either. It then renames them to their destination paths one at a time.

If writing the second temporary file fails, neither destination changes. However, if publishing the second destination fails after publishing the first, the first destination has already changed.

### Why this is undesirable

Export can report failure while leaving an updated `.olp` beside an older or missing `.smmprofile`. The existing test covers failure while preparing the second temporary file, not failure during the second final rename.

### Proposed behaviour

Use recoverable publication with enough information to restore previous outputs or complete publication. Test the final-rename failures and interrupted recovery as well as temporary-file creation.

Two independent file renames do not become one atomic filesystem operation; the documented guarantee must describe what recovery actually provides.

**Relevant code:** [AtomicFile.WriteAll](../../Overlord.Infrastructure/IO/AtomicFile.cs), [ProfileExporterTests](../../Overlord.Tests/Profiles/ProfileExporterTests.cs).

## 14. Error messages can claim nothing changed after a successful install

**Type:** misleading outcome reporting. **Priority:** second and third batches.

### Current behaviour

Some flows perform the installation and then save the profile inside the same broad exception handler. If profile saving fails after the installation commits, the handler can display `Nothing was changed`.

Update All also processes games sequentially. Failure on a later game can reach that message after an earlier game's updates already succeeded.

The Mods page already handles profile-saving failure separately; the other flows should follow the same principle.

### Why this is undesirable

The user may retry an operation or troubleshoot the wrong state because the message contradicts the installed files.

### Proposed behaviour

Report installation results separately from profile bookkeeping and cleanup results. For multiple games, retain and report each completed result before describing a later failure.

**Relevant code:** [UpdatesViewModel.ApplyAsync and UpdateAllAsync](../ViewModels/UpdatesViewModel.cs), [GameInstallViewModel.FixAsync and ActOnFoundAsync](../ViewModels/GameInstallViewModel.cs).

## 15. Mod details do not consistently describe the selected version

**Type:** unfinished interaction and misleading details. **Priority:** fourth batch.

### Current behaviour

Users can choose a compatible exact version and install or switch to it. When an already requested mod is installed and `Latest compatible` is selected, the primary install action is unavailable rather than offering Update.

Dependency rows are chosen from the first compatible version in the version list. The default version description prefers release stability, so those can describe different versions. Changing the version picker does not rebuild the dependency rows or download size.

### Why this is undesirable

The user can see dependencies and a download size that do not describe their selection. Updating from details also requires choosing an exact version manually despite the existing single-mod update service.

### Proposed behaviour

Offer Update under the default selection when a valid newer version is available. Keep explicit version switching, and derive the displayed dependencies and size from the selected version or clearly identified default candidate.

The final dependency plan still needs review because it accounts for the complete installation.

**Relevant code:** [ModItemViewModel](../ViewModels/ModItemViewModel.cs), [ModsViewModel.ShowVersions and DependencyRowsAsync](../ViewModels/ModsViewModel.cs).

## 16. A corrupt catalogue can prevent startup

**Type:** missing startup recovery. **Priority:** fourth batch.

### Current behaviour

The catalogue store opens its database and reads the schema version during construction. If a corrupt database causes construction to fail, application startup logs the failure and shuts down.

There is error handling for catalogue reads after construction, but that path is reached only if service construction succeeded. The existing damaged-database test damages the file after constructing the store.

### Why this is undesirable

Damage to a replaceable catalogue cache can stop the whole application, including access to installed-game information and settings.

### Proposed behaviour

Recognize catalogue corruption during startup, preserve the damaged cache for diagnosis, create a fresh cache and explain that the catalogue needs refreshing. Other failures, such as permission errors or unavailable storage, need their own messages rather than being treated automatically as corruption.

**Relevant code:** [ModCatalogStore constructor](../../Overlord.Infrastructure/Catalog/ModCatalogStore.cs), [App.StartAsync](../App.axaml.cs), [ViewModelErrorTests](../../Overlord.Tests/ViewModels/ViewModelErrorTests.cs).

## 17. Shared archive files can be used by overlapping operations

**Type:** concurrency weakness. **Priority:** fourth batch.

### Current behaviour

The same mod version and platform use the same cached archive and `.part` path across games. Downloads and cache deletion do not share an operation lock. The per-game installer lock is acquired after downloading and does not protect this shared cache.

### Why this is undesirable

Two operations requesting the same archive can contend for its temporary file. Cache deletion can also remove files another operation expects to reuse or open. Depending on timing and platform, this can cause avoidable download or installation failures.

### Proposed behaviour

Coordinate work per archive and use temporary files owned by the operation creating them. Reuse a verified completed result, and prevent cache deletion from deleting archives while they are in use.

**Relevant code:** [ArchiveDownloader](../../Overlord.Infrastructure/Downloads/ArchiveDownloader.cs), [ArchiveCache](../../Overlord.Infrastructure/Settings/ArchiveCache.cs), [ModInstallService.ApplyAsync](../../Overlord.Infrastructure/Installing/ModInstallService.cs).

## 18. Image cache eviction has no explicit bitmap lifetime management

**Type:** resource-lifetime and cancellation concern requiring validation. **Priority:** fourth batch.

### Current behaviour

Decoded bitmaps are cached by URL and width. When the cache reaches its count limit, it is cleared. The code does not explicitly dispose evicted bitmaps, and image requests do not accept cancellation when the user leaves a page.

### Why this is undesirable

Clearing dictionary entries is not explicit disposal of native image resources. Long browsing sessions can retain unnecessary work and resources, but the actual memory impact has not been measured in this review.

Disposing everything immediately on eviction would also be unsafe because visible view models may still reference those bitmaps.

### Proposed behaviour

Define who owns displayed and cached images, release resources once no display needs them, and cancel obsolete work without cancelling a request still needed elsewhere. Validate the result through repeated browsing and memory observation.

**Relevant code:** [ModLogos](../Imaging/ModLogos.cs), [ModsViewModel image loading](../ViewModels/ModsViewModel.cs).

## 19. Remaining presentation and verification work

These are separate from the correctness fixes above.

| Item | Current behaviour | Undesired aspect | Proposed outcome |
| --- | --- | --- | --- |
| Markdown descriptions | Markup is converted to plain text; images appear in a separate gallery. | Authors' formatting, tables and code structure are not fully represented. This is an intentional presentation limitation. | Formatted descriptions with deliberate handling of images and external links. |
| Branding | The main window uses Avalonia's icon. | The application is not visually identified as Overlord. | A reviewed Overlord icon. |
| Activity | Navigation opens a placeholder; file logging exists separately. | There is no in-app operation history or log view. | A separately scoped Activity feature. |
| Progress notes | Some completed features remain listed as open, and the recorded commit is outdated. | The roadmap can lead to duplicate work or mistaken assumptions. | Reconciled progress and roadmap entries. |
| Specifications | Several implementation specifications live in ignored `.claude` files. | A fresh checkout does not contain those design explanations. | Maintained specifications in tracked Markdown files under Docs. |
| Windows and Linux closing | Closing strategies exist, but real-game behaviour remains unverified. | Source inspection and stand-in process tests do not establish clean game shutdown. | Recorded real-game checks on each supported setup. |
| Proton process detection | Name matching includes expected Wine/Linux forms. | Actual running-game detection under Proton has not been demonstrated here. | A focused live detection check. |
| Dedicated-server build detection | The reader searches expected Win64 and Linux locations. | The real server file locations still need confirmation. | Tests based on observed server layouts and a live check. |
| Servers over FTP/SFTP | Navigation opens a placeholder. | Remote deployment is not implemented. | Phase 3 work after local profile switching is reliable. |

Repository visibility was also recorded as outstanding in the progress notes. It was not checked remotely, and changing visibility is not part of this proposal.

## Recommended approval sequence

1. **Transaction safety:** items 1–4, journal compatibility and complete-transaction recovery tests.
2. **Plan and resolver correctness:** items 5–9, shared per-game coordination and accurate operation outcomes.
3. **Profiles:** items 10–14, previewed switching, import and export.
4. **Application resilience and Mods follow-up:** items 15–18, then formatted descriptions.
5. **Documentation and runtime verification:** reconcile the roadmap and verify platform behaviour; scope branding, Activity and remote servers separately.

The recommended first implementation approval remains the transaction-safety batch. Approval of this explanatory document does not itself approve any application-code changes.
