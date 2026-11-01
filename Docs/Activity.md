# Activity

The **Activity** page answers "what did ADA do, and did it work?". Its **Activity** tab lists what ADA changed,
with failures and their reasons. Its **Logs** tab shows ADA's own log files, for diagnosing problems and filing
bug reports.

## Using it

### The Activity tab

- **The list** is newest first, grouped by day ("Today", "Yesterday", "Thursday 29 October", with the year for
  earlier years). Each row shows:
  - the time and an icon for its area;
  - the summary, red for a failure;
  - the install, and "Cancelled" for a cancellation.
- **Clicking a row** with details expands it. It shows the full date and time, the error the user saw, and the
  detail lines (`SML 3.11.0 → 3.12.0`, a backup's file name, …). The expanded lines can be selected and copied.
- **Filters:**
  - **area:** Mods, Profiles, Backups, Saves, Servers, Game;
  - **install:** every install in the history, including servers since removed. Installs recorded under the same
    name, such as two Epic copies, also show their folder;
  - **Failures only**;
  - **search** across summaries, installs, errors and details.
- **Showing more:** the 200 newest matches are shown, with **Show 200 more** at the bottom.
- **Live updates:** something done on another page appears at once.
- **Clear history…** asks first, then deletes every entry. Mods, profiles and backups aren't affected.
- **Show folder** opens `ADA/Activity`.

### The Logs tab

- **The day picker** lists each day with a log file, with its size ("Today · 285 KB").
- **The level filter** has All levels, Warnings and errors, and Errors only.
- **Search** covers messages and stack traces.
- **The list** shows one row per logged message, oldest first, scrolled to the newest:
  - each row shows the time, the level (warnings amber, errors red) and the message on one line;
  - messages over 300 characters are shortened in the list only;
  - "(+ details)" marks a message with a stack trace.
- **Selecting a row** shows the whole message and its stack trace below the list, selectable, in a monospaced
  font.
- **Copy** puts the shown messages on the clipboard exactly as written: the matches when a filter or search is
  set, otherwise the whole day.
- **Refresh** re-reads. The tab also re-reads each time it's opened, but doesn't update live.
- **Show folder** opens `ADA/Logs`.

## What gets recorded

Each entry is recorded when an operation ends: succeeded, failed (with the message the user saw) or cancelled.
A refusal counts as a failure. For example, an install refused because the game is running reads "Mod change
failed" with the reason as its error.

| Area | Recorded | Example summary | Details |
|---|---|---|---|
| Mods | Install, update, uninstall (Mods page, queue, a save's "install missing") | "Installed 2 mods, updated 1" | `AutoTrains 1.3.8`, `SML 3.11.0 → 3.12.0`, `Removed X 1.0.0`; for a failure, what it set out to do |
| Mods | Repair | "Repaired 2 mods", or "Couldn't repair 1 mod" (failed) | each mod, or why it couldn't be repaired |
| Mods | An interrupted change finished at launch | "Finished an interrupted mod change" | completed, discarded and undone mods |
| Profiles | Create, delete | "Created profile Atria (12 mods)" | – |
| Profiles | Switch, or update the active profile | "Switched to Atria: installed 287 mods" / "Updated Atria: updated 3 mods" | the mod changes |
| Profiles | Take over or save found mods | "Took over 5 mods into Atria" / "Saved 5 found mods as profile Later" | the mods |
| Profiles | Import or export a profile file | "Imported profile Atria from Mods.smmprofile" / "Exported Atria to Atria.olp" | – |
| Backups | Back up by hand or before a mod change | "Backed up Saves (2 MB)" | the file name; files skipped because they kept changing |
| Backups | Restore | "Restored Saves (12 files)" | the backup; the safety copy, or that none was needed; ADA settings pending |
| Backups | Delete | "Deleted the backup from 01/11/2026 18:34" | the file name |
| Saves | Copy, import or export blueprints | "Copied 2 blueprints to Verdana, 1 renamed" | the names, and renames |
| Servers | Connect, disconnect (the user's choice) | "Connected to Box" / "Connecting to Box failed" | – |
| Servers | Take over a server's lock | "Took over Box's lock" | who held it |
| Servers | Push or put back mod settings | "Pushed settings for 5 mods to Box (Dedicated Server)" | the mods |
| Game | The game closes | "Played Satisfactory for 1 h 20 m" | – |

**Not recorded:**
- successful scheduled backups (one an hour would crowd out everything else; failures are recorded);
- pruning old automatic backups;
- reconnects at launch and background retries;
- catalogue refreshes, searches and settings changes.

A profile switch is one entry: the mod changes it makes are its details, not a second entry. "Nothing to back
up yet" is only a failure for a backup made by hand.

Game sessions:
- They aren't tied to an install, because a running game can't be matched to one.
- If the game was already running when ADA started, the entry reads "for at least …".

## The history file

`ADA/Activity/activity.jsonl` holds one JSON object per line, oldest first:

```json
{"id":"640b4375-…","atUtc":"2026-11-01T18:34:34.86Z","area":"Backups","action":"BackupRestored","outcome":"Succeeded","summary":"Restored Saves (12 files)","install":"Satisfactory (Epic)","installKey":"/Users/…/Satisfactory","details":["From ADA 2026-11-01 183434 Manual.zip","Safety copy: ADA 2026-11-01 183434 BeforeRestore.zip"],"error":null}
```

- **`area` and `outcome`** are enums written as text.
- **`action`** is free text (`ActivityActions`), so an older ADA can still read entries a newer one records.
  Entries whose area it doesn't know are skipped.
- **`installKey`** is `GameInstall.GamePath`, which the install filter uses.
- **Escaping:** apostrophes and accented letters are written as they are, not escaped.

| Situation | Behaviour |
|---|---|
| Recording | Appended under a lock and flushed. Never throws: a failed write is logged, and the operation it describes carries on. |
| A crash mid-write | The torn line is skipped on read, ended before the next append, and dropped at the next trim. |
| Retention | At startup, entries older than **a year** are dropped, then the oldest beyond **10,000**. The file is rewritten in one step (`AtomicFile`), and only when something changed. |
| Clear history | Deletes the file. |

## The log files

ADA logs with Serilog to `ADA/Logs/ada-yyyyMMdd.log`, one file per day, keeping 7. A second ADA running the same
day writes `ada-yyyyMMdd_001.log`, which the Logs tab shows as part of that day. Each message is written as:

```
2026-11-01 18:40:05.511 +00:00 [ERR] Repair failed in /Users/…/Satisfactory
ADA.Infrastructure.Installing.InstallException: Not enough free space: …
   at ADA.Infrastructure.Installing.ModInstallService.ApplyReservedAsync(…)
```

- **Message boundaries:** a line starting with a timestamp starts a message, and the lines up to the next one (a
  stack trace) belong to it.
- **Level codes:** `VRB`, `DBG`, `INF`, `WRN`, `ERR`, `FTL`. Information is the lowest level written, and HTTP
  requests are logged only as warnings.
- **The file being written:** it's opened sharing read and write, so today's file can be read while ADA writes it.
- **Very large files:** only the last 20 MB of each file is read, starting at a whole line, and the tab says so.
- **Mod lists:** lines that would list every mod, such as "Resolved 286 mods …", show the first 10 and
  "and N more" (`LogText.Sample`). The full list of a real change is in Activity's details.

### The size limit

Settings → **LOGS** → **Logs folder limit** caps the folder: 100 MB, 250 MB, 500 MB, 1 GB, **2 GB** (the default)
or 5 GB (`AppSettings.MaxLogFolderMegabytes`). The section also shows how much the logs use, and where they are.

- **While ADA runs, Serilog enforces it.** Each file may grow to a seventh of the limit, less 1 MB
  (`LogFiles.FileLimitFor`), and 7 files are kept (`LogFiles.FILES_KEPT`). Serilog writes the message that
  crosses a file's limit before it stops, so the 1 MB per file is headroom for that message. Seven full files
  therefore can't exceed the limit.
- **A full file stops logging for the rest of that day** (`rollOnFileSizeLimit: false`). Rolling to a new file
  would keep the newest messages, but would also mean "7 files" no longer means "7 days". A day only fills its
  share in a flood, and the flood's start, which shows its cause, is already in the file.
- **Older files are trimmed** at startup, and as soon as the limit is lowered on the Settings page. The oldest of
  ADA's log files are deleted until the folder fits (`LogFiles.TrimToAsync`). The file last written is never
  deleted, and neither are files that aren't ADA's daily logs.
- **A new limit applies to new log files at the next start.** Serilog's limits are fixed when the logger is
  created. Restored ADA settings are applied before it's created, so a restored limit applies on the start that
  restores it.

## How it works

| Piece | Project | Job |
|---|---|---|
| `ActivityArea`, `ActivityOutcome`, `ActivityActions`, `ActivityEntry` | Core | The model |
| `ActivityLog` | Infrastructure | Append, read newest first, trim, clear; raises `Recorded` and `Cleared` outside its lock, on the thread that caused them |
| `ActivityRecording.RecordAsync`, `ActivityDescription` | Infrastructure | Runs an operation and records how it ended |
| `ActivityText` | Infrastructure | Mod-change summaries and detail lines |
| `LogFiles`, `LogText` | Infrastructure | Reading the log files; sampling long lists in log lines |
| `ActivityViewModel`, `ActivityEntryViewModel`, `ActivityDayViewModel`, `ActivityFilterOption<T>` | ADA | The Activity tab |
| `LogsViewModel`, `LogLineViewModel` | ADA | The Logs tab |

**Recording.** Each service that records takes `ActivityLog? activity = null` as its last constructor parameter:
`ModInstallService`, `GameProfiles`, `ProfileTransfer`, `BackupService`, `ServerConnections`, `ConfigPush`,
`SavesViewModel` and `DashboardViewModel`. DI passes the registered log; tests that don't pass one are unaffected.

`RecordAsync` is an extension method on `ActivityLog?`:
- **No log:** it only runs the work.
- **Success:** it records the operation's description, or nothing when the description is null.
- **Failure:** it records the failure summary with the exception's message, then rethrows. Exceptions marked as
  not failures are rethrown without an entry.
- **Cancellation:** it records the failure summary with " failed" replaced by " cancelled".

Each public method wraps a private `…CoreAsync` holding the original body. `TransactionResult.PreviousVersions` is
read before a change, so updates can show the version they replaced.

Blueprints are recorded by `SavesViewModel` rather than `BlueprintTransfer`, because only the page knows whether
it's copying or importing. Game sessions are recorded by `DashboardViewModel`, which already watches the game.

**The page.**
- **Loading:** it reads the history once, the first time it's shown. After that, `Recorded` and `Cleared` keep it
  current, posted to the page's thread. Entries recorded during that first read are merged by id, so none is
  missed or listed twice.
- **Size:** the day groups are nested lists, which Avalonia can't virtualise, hence the 200 at a time. The Logs
  tab is one flat `ListBox`, so it shows a whole day.
- **Re-reading the logs:** Avalonia builds a tab's content only while it's selected, so `LogsView.OnLoaded`
  re-reads the log each time the tab is opened.

**Startup.** `App` calls `ActivityLog.TrimAsync()` once the services are built. `AppPaths.Activity` is created with
the other folders.

## Limits and open questions

- **No undo:** "put these versions back" from an entry would be a natural follow-up, but needs its own design.
- **No history from before Activity:** it starts empty, and isn't filled in from older logs.
- **Server saves:** blueprint actions on server installs aren't possible yet, so aren't recorded.
- **Backup sizes in summaries** are whole MB ("0 MB" for a tiny backup). The page's own `ByteSize` lives in the app
  project, so Infrastructure uses the coarser `DiskSpace.Format`.
- **Not live:** the Logs tab re-reads when opened and on Refresh only.
- **To check in the real app:**
  - entries appearing while another page works;
  - the dialog asking before Clear history;
  - Copy pasting into another app;
  - Show folder on each OS.
