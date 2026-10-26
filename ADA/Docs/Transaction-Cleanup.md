# Transaction commitment and cleanup

Implemented on 25 October 2026, following approval of the Fix 1 adjustment.

## Update order

For an update from Mod A V1 to V2:

1. Download V2, then extract and verify it in the transaction working folder.
2. Move V1 from `Mods` into the transaction working folder.
3. Move the verified V2 folder into `Mods` and update the installed record.
4. Persist `committed: true` in the transaction journal once every planned change is complete.
5. Delete V1 and the remaining transaction working files.
6. Delete the journal.

The final marker covers the whole transaction. When several mods change together, old versions are retained until all planned changes have completed and the marker has been saved.

## Failure behaviour

Before commitment, an ordinary installation failure still attempts rollback using the retained old folders.

After commitment, an I/O or access failure during cleanup is logged. The installation returns its successful result, keeps V2 and its installed record, and leaves the committed journal for recovery. Cleanup failure never enters the installation rollback handler.

Recovery of a committed journal only deletes leftover working files and the journal. It does not replay mod changes or rewrite installed records. If cleanup fails again, the exception is reported through the existing calling flow and the journal remains for another retry. A new folder transaction cannot replace that journal until recovery finishes.

## Compatibility and scope

Older journals have no `committed` field; it defaults to false. Their existing recovery policy remains: complete verified changes and uninstalls, and discard unfinished installs. After that recovery outcome is established, it is marked committed before cleanup begins.

This adjustment does not implement the separately proposed complete-transaction crash recovery policy, requested-mod journaling, profile switching or other reviewed fixes.

## Relevant code and checks

- [Journal model](../../ADA.Infrastructure/Installing/InstallRecords.cs)
- [Installation, recovery and cleanup](../../ADA.Infrastructure/Installing/ModInstaller.cs)
- [Application logging integration](../../ADA.Infrastructure/Installing/ModInstallService.cs)
- [Cleanup regression tests](../../ADA.Tests/Installing/TransactionCleanupTests.cs)
- [Existing crash and rollback tests](../../ADA.Tests/Installing/CrashSafetyTests.cs)

Regression cases cover updates in the same location and across `GameFeature` locations, failures before and after backup deletion, a partly deleted backup, repeated cleanup failure, uninstall cleanup, refusal to overwrite a pending journal, and legacy journals.

Release compilation passed. A full test run passed 311 of 311 tests. An earlier full run failed one existing Mods-page search test before it invoked installation; that test passed in isolation and the full suite subsequently passed. This indicates an intermittent failure, whose cause was not established or changed in this adjustment.

No live game installation or gameplay validation was performed.
