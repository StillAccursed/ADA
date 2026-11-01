# Backups

The **Backups** page backs up an install's saves, blueprints and configs, and ADA's own settings, by hand or on a
schedule, and restores any part of a backup. Every restore can be undone.

## Using it

- **Back up now.** Tick the parts and press the button. A progress bar shows the file being copied, with
  Cancel. Parts the install can't have are greyed out, with the reason as a tooltip.
- **Schedule.** Changes are saved as soon as they're made:
  - how often: off, every hour, 3, 6 or 12 hours, every day or every week;
  - which install, and which parts;
  - how many automatic backups to keep;
  - whether to back up saves before mod changes;
  - the backup folder, with **Change…** and **Use default**.

  A status line shows when the next backup is due, or why the last one failed.
- **The list** holds backups of every install, newest first. Each shows when, why ("Made by hand",
  "Scheduled", "Before a mod change", "Safety copy taken before a restore"), the install it came from, its
  parts, file count and size. Each has **Restore…** and a bin button, which asks before deleting.
- **Restore…** opens a page in place of the list:
  - tick the parts (ADA settings start unticked);
  - a warning appears if the backup came from a different install;
  - press **Restore**. The result names the safety copy.

  After restoring ADA settings, choose **Restart ADA now** or **Later**.
- **Show folder** opens the backup folder.

## The parts

| Part | Folder | Left out |
|---|---|---|
| Saves | `<user data>/Saved/SaveGames` | the `blueprints` folder |
| Blueprints | `<user data>/Saved/SaveGames/blueprints` | – |
| Game settings | `<user data>/Saved/Config` | `CrashReportClient` |
| Mod settings | `<game>/FactoryGame/Configs` | – |
| ADA settings | ADA's `Config` folder (settings, profiles, saved servers; never secrets) | – |

`*.tmp` files and symbolic links are skipped. The first three parts need the install's user-data folder (see
[Saves.md](Saves.md)); mod settings need a local install; ADA settings are always available. A server install
can therefore only back up ADA settings for now.

## The backup file

One `.zip` per backup, named like `ADA 2026-10-31 133032 Manual.zip`. By default backups go in
`ADA/Backups`; the folder can be changed in the Schedule box. Inside:

```
manifest.json                      ← written last
Saves/<account>/Atria_autosave_0.sav
Blueprints/Atria/…
GameConfig/Windows/Engine.ini
ModConfig/AutoRun.cfg
AdaConfig/settings.json
```

`manifest.json` records:
- format version, id, creation time (UTC) and trigger;
- which parts the backup holds;
- the source install (display name, game folder, user-data folder);
- every file's path, size and SHA-256;
- any files skipped because they kept changing.

The page reads only manifests, so listing stays fast. `.sav` files are stored uncompressed (they're already
compressed inside); everything else is compressed.

## Rules

| Situation | Behaviour |
|---|---|
| Writing a backup | Written as `….zip.partial`, flushed, then renamed. A crash or cancel never leaves something that looks finished; leftover `.partial` files are removed before the next backup. |
| Free space | Checked first: total size × 1.1 must fit, or it fails with the usual message. |
| Nothing to back up (e.g. a never-launched install) | Refused with "There's nothing to back up yet…" (`NothingToBackUpException`). Nothing is written. |
| The game is running | Backups are allowed. A file whose size or time changes while being read is retried up to 3 times, half a second apart, then skipped and listed, so a half-written autosave never goes in. |
| Restoring while the game runs | Refused. |
| Damaged or tampered backup | Every chosen file's SHA-256, and its path, is checked **before anything is touched**. Paths must pass `GamePaths.IsSafe`. Any problem, and nothing is restored. |
| Before a restore | A safety copy of the same parts is taken (trigger `BeforeRestore`), unless they hold no files yet. |
| What a restore writes | Each file replaces the current one in one step (`AtomicFile`). Files not in the backup are left alone, so a save made after the backup survives. |
| Restoring ADA settings | Written to `Staging/PendingConfig`, then moved into `Config` at the next start, before any settings store loads (`PendingAdaConfig.Apply` in `App`). The running app's stores cache what they read and would overwrite restored files. |
| Two operations at once | One backup or restore at a time; the other waits. |

## Schedule and retention

- **When:** the scheduler waits 1 minute after launch, then checks every minute **while ADA is open**. There is
  no OS-level scheduler. A backup is due when the chosen install's newest **scheduled** backup is older than
  the interval, or there is none. The time comes from the backup folder, so restarting ADA doesn't reset it,
  and an overdue backup runs at the first check (catch-up).
- **Failures:**
  - A real failure retries after an hour (sooner if the interval is shorter).
  - "Nothing to back up yet" waits a whole interval.
  - The status line shows why in both cases.
- **Retention:** after every automatic backup, older backups of the **same kind** from the **same install**
  beyond *Keep* (default 10, range 1–100) are deleted, oldest first. Kinds: scheduled, before a mod change,
  safety copy before a restore. **Hand-made backups are never deleted.** Two backups are from the same install
  when their user-data folders match, and their game folders match where both recorded one.
- **Changing the folder:** old backups stay in the old folder; the page lists the new one from then on.

## Backing up before mod changes

When on, `ModInstallService` calls the scheduler's hook before installing, uninstalling or switching profiles
on a local install, after the game-running check. It backs up **Saves** with trigger `BeforeModChange`:
- **At most once per install every 10 minutes**, so a run of changes makes one backup.
- **No saves yet** never blocks the change.
- **Any other failure refuses the mod change:** "Couldn't back up your saves before changing mods, so nothing
  was changed: … You can turn this off on the Backups page."
- **Repairs don't trigger it**, since they only put back files that are already installed.

## How it works

| Piece | Project | Job |
|---|---|---|
| `BackupPart`, `BackupTrigger`, `BackupManifest`, `BackupInfo`, `RestoreResult`, `BackupException`, `NothingToBackUpException` | Core | The model |
| `BackupService` | Infrastructure | Create, list, restore, delete and prune. Its folder comes from a delegate read on each call; it raises `BackupCreated` after every backup, safety copies included |
| `BackupScheduler` | Infrastructure | The schedule loop, retention (via `BackupCreated`), and the before-mod-change hook |
| `PendingAdaConfig` | Infrastructure | Applies restored ADA settings at startup |
| `BackupsViewModel`, `BackupScheduleViewModel`, `RestoreBackupViewModel`, `BackupItemViewModel`, `PartOptionViewModel` | ADA | The page |

Wiring notes:
- `ModInstallService` gets the hook as a delegate that resolves `BackupScheduler` when called, because the
  scheduler's dependencies lead back to `ModInstallService`.
- `MainViewModel.Start` starts the scheduler.
- `App` handles **Restart ADA now**: it starts the same executable with the same arguments (passing the `.dll`
  on when run through the `dotnet` host), then shuts down.

Settings (`AppSettings`): `BackupFolder`, `BackupIntervalHours` (one of 0, 1, 3, 6, 12, 24, 168),
`BackupInstall`, `BackupParts`, `KeepAutomaticBackups`, `BackupBeforeModChanges`.

## Limits and open questions

- **Not backed up:** saves on dedicated servers, and anything while ADA is closed.
- **Restore granularity:** whole parts only, not single files from inside a backup.
- **Moving old backups:** changing the folder doesn't move them.
- **To check in the real app:**
  - a scheduled backup a minute after turning the schedule on;
  - **Restart ADA now**;
  - a mod install with "back up before mod changes" on.
- **On macOS,** restart launches the executable inside the app bundle directly, which may briefly show a second
  Dock icon. `open -n` on the bundle is the alternative if that matters in a packaged build.
