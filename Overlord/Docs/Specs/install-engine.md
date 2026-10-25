# Install engine

Status: implemented 2026-10-25 in `Overlord.Infrastructure/Installing/` (`ModInstaller`, `ModArchive`). Crash safety is proven by fault-injection tests (`Overlord.Tests/Installing/CrashSafetyTests.cs`): a transaction is killed at every step, recovery is killed at every step, and a rollback is killed at every step; each time every mod ends wholly old or wholly new and the record matches `Mods`.

## The rule

**The game-visible locations (`FactoryGame/Mods/` and `FactoryGame/Mods/GameFeatures/`) only ever change by renaming a whole mod folder.** Files are never written, overwritten or deleted inside them one at a time. A crash, force-quit or power cut can therefore leave a mod fully old, fully new, or (for an instant during an update) absent, but never partly written.

Overlord refuses to start a transaction while the game (or, for servers, the server) is running, because the game locks mod files. Detection (`GameProcesses`) looks for processes whose executable name starts with `FactoryGame` or `FactoryServer`. On Windows it uses .NET's process names. On macOS and Linux it reads the command-name column of `ps` (never the arguments, which contain login tokens), because under CrossOver .NET reports every Wine process as a module Wine loaded (`EOSOverlayRenderer-Win64-Shipping.exe`) while `ps` shows the Windows path, e.g. `Y:\Games\Satisfactory\Engine\Binaries\Win64\FactoryGameEGS-Win64-Shipping.exe`. Verified on CrossOver with the game running, 2026-10-25; Proton is still to be confirmed.

## Folders

| Folder | Where | Purpose | Lifetime |
| --- | --- | --- | --- |
| `Staging/Archives/` | App data (see `app-data.md`) | Downloaded `.smod` files | Kept or deleted per the **Keep downloaded archives** setting |
| `FactoryGame/.overlord/` | Game folder, beside `Mods` | Hidden working area on the **game's own drive** | Permanent; holds only `installed.json` and `lock` when idle |
| `FactoryGame/.overlord/installed.json` | Inside the above | Which mods Overlord installed: version, location, and every file's path, size, SHA-256 and modified time | Permanent, written atomically after each commit |
| `FactoryGame/.overlord/lock` | Inside the above | Held during a transaction so two Overlord windows can't change one game at once | Permanent |
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

1. Rename `Mods/.../<Reference>` → `.overlord/txn-<id>/old/<Reference>`.
2. Delete it there when the transaction ends.

Only mods in `installed.json` can be uninstalled or replaced. A folder Overlord did not put there (a manual install, another tool's) is never overwritten or removed; the transaction is refused before anything changes.

Deleting thousands of files directly in `Mods` could be interrupted halfway; deleting them outside `Mods` cannot leave the game a partial mod.

## Crash recovery (on launch)

If `.overlord/journal.json` exists, a transaction was interrupted:

- A mod journaled "ready" (verified in `new/`) is committed: roll forward. Uninstalls are also completed.
- A mod not yet verified is discarded; `Mods` was never touched for it.
- If the journal says a rollback was under way, the rollback is finished instead: every mod goes back to how it was before the transaction.
- Then `txn-<id>/` is deleted and the journal removed. The next transaction also runs recovery first, automatically.

The journal is written and flushed before each step it describes. Because a crash can still land between a rename and the journal update after it, every commit and rollback step checks where folders actually are rather than trusting the journal alone, so recovery is safe to repeat any number of times (including after a crash during recovery).

## Failures while running

Anything that goes wrong mid-transaction (a failed rename, a verification mismatch, cancellation) rolls back **every** change in the transaction, newest first: new versions are moved out of `Mods` to `txn-<id>/trash/`, old versions are moved back from `old/`, and `installed.json` is restored. Before anything is changed, every archive is opened and validated, unmanaged folders are checked for, and free space on the game's drive is checked (uncompressed size plus 64 MB).

## Startup check

Catches damage the journal cannot see: manual edits, disk errors, other tools.

- **Fast check, every launch:** compare each installed mod's files against its recorded file list by size and modified time. Milliseconds.
- **Full check:** re-hash a mod's files when the fast check finds a difference, or for all mods via a **Verify files** action.
- **Repair:** a damaged or incomplete mod is reinstalled through the normal install steps, from `Staging/Archives/` if the archive is kept, otherwise by downloading it again.
- Mods present in `Mods` that Overlord did not install are listed as "unmanaged" and left alone.

## In the app

`ModInstallService` (`Overlord.Infrastructure/Installing/ModInstallService.cs`) is the app's single entry point:

- **On launch**, for each detected game: run recovery, then the fast check, and show the result on the game's Dashboard card ("3 mods installed · all files OK", or which mods need repair, plus folders Overlord does not manage). A game Overlord has never changed is only read: no `.overlord` folder is created until the first install. If an interrupted transaction is found while the game is running, recovery waits and the card says so.
- **Install:** refuse if the game is running, then download every archive first (with the **Download retries** setting), then one `ModInstaller` transaction, then delete the used archives if **Keep downloaded archives** is off. A failed download or a rolled-back install keeps its archives, so a retry does not download again.
- **Repair** (the card's **Repair** button): reinstall each damaged or missing mod at its installed version, from a kept archive if it still matches, otherwise downloaded again. A version ficsit.app no longer lists is reported rather than replaced.
- Clients always install `Windows` builds (also under CrossOver and Proton); dedicated servers install `WindowsServer` or `LinuxServer`.
- **While Satisfactory is running**, the Dashboard shows a banner (checked every 3 s) and Repair is disabled. **Close Satisfactory** asks for confirmation (unsaved progress is lost), sends a polite quit, waits up to 15 s, then forces any game process still running. When the game closes, every game card is re-checked and any recovery that was waiting runs.

  | OS | Polite quit | Status |
  | --- | --- | --- |
  | Windows | `CloseMainWindow` (the window's close message) | Not yet tested with the game |
  | macOS (CrossOver) | The standard Mac **Quit** request to the game's app process (`NSRunningApplication.terminate`), which Wine passes to the game as a Windows session end | **Confirmed 2026-10-25:** the game quits cleanly from the Quit request. `SIGTERM` was tried first and is ignored by Wine: the game had to be forced (2026-10-25). Running Wine's `taskkill` inside the bottle with CrossOver's command-line `wine` failed (`failed to load start.exe`) |
  | Linux (Proton) | `SIGTERM` | Not yet tested; likely ignored like on CrossOver |

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
