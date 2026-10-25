# Install engine

Status: design approved 2026-10-25, not yet implemented (Phase 1).

## The rule

**The game-visible locations (`FactoryGame/Mods/` and `FactoryGame/Mods/GameFeatures/`) only ever change by renaming a whole mod folder.** Files are never written, overwritten or deleted inside them one at a time. A crash, force-quit or power cut can therefore leave a mod fully old, fully new, or (for an instant during an update) absent, but never partly written.

Overlord refuses to start a transaction while the game (or, for servers, the server) is running, because the game locks mod files.

## Folders

| Folder | Where | Purpose | Lifetime |
| --- | --- | --- | --- |
| `Staging/Archives/` | App data (see `app-data.md`) | Downloaded `.smod` files | Kept or deleted per the **Keep downloaded archives** setting |
| `FactoryGame/.overlord/` | Game folder, beside `Mods` | Hidden working area on the **game's own drive** | Permanent, empty when idle |
| `FactoryGame/.overlord/txn-<id>/` | Inside the above | One transaction's work | Deleted when the transaction ends |
| `FactoryGame/.overlord/journal.json` | Inside the above | Write-ahead journal | Present only while a transaction is open |

`.overlord` is hidden: the leading dot hides it on macOS and Linux, and Overlord also sets the Hidden attribute on Windows. It is outside `Mods`, so the game never scans it.

Because `.overlord` is on the same drive as `Mods`, every move between them is a rename: instant, all-or-nothing, and no file data is written. Each mod file is written to disk exactly once, the same as extracting straight into `Mods`.

## Install (per mod)

1. **Download** the `.smod` to `Staging/Archives/<Reference>/<Version>-<Target>.smod`, or reuse a kept copy (see Downloads below).
2. **Extract** the mod root (flat or multi-target, see `smod.md`) into `.overlord/txn-<id>/new/<Reference>/`.
3. **Verify** every extracted file: write errors are fatal, then re-read each file and compare size and SHA-256 with the archive entry. Flush to disk.
4. **Record** the file list (path, size, SHA-256) for the startup check, and journal "ready to commit".
5. **Commit:** rename `new/<Reference>` into `Mods/<Reference>` or `Mods/GameFeatures/<Reference>` (per `GameFeature`). Journal "committed".

Mods are committed one at a time as each is ready, dependencies first (SML before the mods that need it), so `Mods` fills up mod by mod while a profile installs. Progress inside a mod (files extracted) is shown in Overlord's UI, not in `Mods`.

## Update

1. Steps 1–4 above for the new version.
2. Rename `Mods/.../<Reference>` → `.overlord/txn-<id>/old/<Reference>` (old version out, instant).
3. Rename `.overlord/txn-<id>/new/<Reference>` → `Mods/.../<Reference>` (new version in, instant).
4. When the whole transaction has succeeded, delete `old/`.

Until step 4, the old version is intact in `old/`, so a failed transaction can be rolled back completely. If `GameFeature` changed between versions, the old and new locations differ; the same two renames apply.

## Uninstall

1. Rename `Mods/.../<Reference>` → `.overlord/txn-<id>/trash/<Reference>`.
2. Delete it there.

Deleting thousands of files directly in `Mods` could be interrupted halfway; deleting them outside `Mods` cannot leave the game a partial mod.

## Crash recovery (on launch)

If `.overlord/journal.json` exists, a transaction was interrupted:

- A mod journaled "ready to commit" (verified in `new/`) is committed: roll forward.
- A mod not yet verified is discarded from `new/`; if its old version is in `old/`, it is renamed back.
- Then `txn-<id>/` is deleted and the journal removed.

The journal is written and flushed before each step it describes, so it never claims more than the disk holds.

## Startup check

Catches damage the journal cannot see: manual edits, disk errors, other tools.

- **Fast check, every launch:** compare each installed mod's files against its recorded file list by size and modified time. Milliseconds.
- **Full check:** re-hash a mod's files when the fast check finds a difference, or for all mods via a **Verify files** action.
- **Repair:** a damaged or incomplete mod is reinstalled through the normal install steps, from `Staging/Archives/` if the archive is kept, otherwise by downloading it again.
- Mods present in `Mods` that Overlord did not install are listed as "unmanaged" and left alone.

## Downloads

Implemented in `Overlord.Infrastructure/Downloads/ArchiveDownloader.cs`. It only ever returns an archive whose size and SHA-256 match the API.

- A download is written to `<file>.smod.part`, hashed while it streams, flushed to disk, and only then renamed to `.smod`. A rejected or interrupted download never leaves a file behind.
- **Retried** (up to the **Download retries** setting, default 5, maximum 8, after the first attempt): network errors, timeouts, HTTP 5xx, HTTP 408, HTTP 429 (honouring `Retry-After`), **wrong size**, **wrong hash**. A download that grows past the expected size is stopped at once.
- **Not retried:** other HTTP 4xx (e.g. 404, 403); the file will not appear by asking again.
- Backoff between attempts: 1 s, 2 s, 4 s, … capped at 30 s.
- **Kept archives are checked before reuse.** One whose size or hash no longer matches is deleted and downloaded again.
- When every attempt fails, the error lists each attempt and why it failed, for the Activity log.
- Mod references and versions are checked before being used in file names, so nothing can write outside `Staging/Archives/`.

## Setting: Keep downloaded archives

- **On (default):** archives stay in `Staging/Archives/`. Switching profiles back, rolling back and repairing work offline and without re-downloading.
- **Off:** an archive is deleted once the mod using it is committed. Repair and reinstall download again.
- Turning it off does not delete existing archives; the Settings page shows their total size and offers **Delete archives**.
