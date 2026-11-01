# Saves

The **Saves** page lists the selected install's playthroughs (sessions), their saves and blueprints, what each
save was made with, and gets a save's mods. Everything here reads save files; nothing writes to a save.

## Using it

Three levels, with a breadcrumb (`Saves › Atria › Atria_autosave_2`) to go back to any of them:

1. **Sessions.** One item per playthrough, in List, Grid or Card layout (remembered), with search (session
   and save names) and sort (last saved, name, playtime). A session that has blueprints but no saves is
   listed too, so blueprints can be copied into it.
2. **A session.** Its saves, newest first, and its **Blueprints** tab:
   - tick blueprints, then **Copy** them to another session chosen in the drop-down;
   - **Export…** the ticked ones (all of them when none are ticked) to a `.zip`;
   - **Import…** from `.zip` files or loose `.sbp` files.
3. **A save.**
   - **Mods** tab: the mods recorded in the save, each marked *Installed*, *&lt;version&gt; installed* or
     *Not installed* against the selected install.
   - **Options** tab: Advanced Game Settings, tutorial, visibility, partitioned world, save id, and the mods'
     per-session settings grouped by mod.
   - Side column: playtime, when saved, size, game build, format, **Show in folder**, and two actions:
     - **Switch to this save's mods** opens the Switch profile dialog for a new profile holding exactly the
       save's mods at their recorded versions, named after the session ("Atria", or "Atria (2)" if that's
       taken). Nothing is saved unless the switch succeeds.
     - **Install N missing mods** adds the missing ones to the Mods page's queue and opens it on the Queued
       filter. Nothing installs until **Install queued** is pressed there, and nothing is removed.

The page re-reads each time it's shown, and while shown whenever the game writes a save or mods are
installed or removed.

## Where the files are

| What | Path, under the install's user-data folder |
|---|---|
| Saves | `Saved/SaveGames/<account id>/*.sav`; a dedicated server's are in `Saved/SaveGames/server/` |
| Blueprints | `Saved/SaveGames/blueprints/<session name>/<Name>.sbp` and optional `<Name>.sbpcfg` |

The user-data folder is `GameInstall.UserDataPath`, filled in when installs are detected:

| Install | User-data folder |
|---|---|
| Windows (Steam or Epic) | `%LOCALAPPDATA%\FactoryGame`, shared by both stores |
| CrossOver | The bottle's `Local AppData` from its `user.reg`, else `drive_c/users/crossover/AppData/Local`, plus `FactoryGame` |
| Linux, Steam (Proton) | The prefix in `compatdata/526870/pfx`: the game's library first, then the Steam root |
| Linux, native dedicated server | `~/.config/Epic/FactoryGame` |
| Heroic | The prefix in Heroic's `GamesConfig/<app>.json` (`pfx/` for Proton) |
| Legendary without Heroic, remote servers | Unknown (null), so the page explains it can't show saves |

The path is where the folder *is or will be*: it doesn't depend on the folder existing, so an install doesn't
change identity when the game first runs.

### What counts as a save

Only `.sav` files directly inside an account folder. Left out on purpose:
- `SaveGames/ServerManager_V2.sav`, the game's server list, not a world;
- anything under `blueprints`;
- copies in sibling folders such as `SaveGames_backup`;
- names like `x.sav_bak`, since the extension is compared exactly.

A `.sav` that can't be read is reported ("1 save couldn't be read…"), never silently dropped.

## How it works

| Piece | Project | Job |
|---|---|---|
| `SaveHeaderReader`, `SaveMapOptionsParser` | Platform | Read a save's uncompressed header only (about 36 KB) |
| `SaveHeader`, `SaveMapOptions`, `SaveGame`, `Blueprint`, `SaveFolderContents` | Core | What the page shows |
| `SaveLibrary`, `SaveFolderWatcher` | Infrastructure | List a user-data folder; watch it, one event per burst |
| `BlueprintTransfer` | Infrastructure | Copy, export and import blueprints |
| `GameUserDataPaths`, `WinePrefix.LocalAppData` | Platform | Work out the user-data folder per host |
| `SavesViewModel` and its item, session and save view models | ADA | The page |

### The save header

Read field by field, stopping after the "is modded" flag (header version 14 also reads the tail fields).
Verified against real 1.2 saves (header 14, save 60, build 513885):

| Field | Notes |
|---|---|
| Header version, save version, build | `int32` each |
| Save name | `FString`, header 14+ only; older saves use the file name |
| Map name, map options | Map options are `?key=value…`; account ids (`ClientIdentity`, `PlayerInfoSnapshot`, `FallbackAccountNames`) are dropped while parsing, so they never reach the UI or logs |
| Session name, playtime (seconds), saved at (.NET ticks, read as UTC), visibility | |
| Mod metadata | JSON `{"Mods":[{"Reference","Name","Version"}]}` |
| Is modded | |
| Save id, partitioned world, MD5, Advanced Game Settings | Header 14 only; other versions show "Unknown" |

Header versions 8–64 are accepted, newer ones only if the fields pass sanity checks; anything else isn't a save.

**Session settings** in the map options are base64 Unreal `FVariant`s (type tag, byte count, bytes). Decoded
types: bool (2), float (10), int32 (14), linear colour (17, shown converted to sRGB). Others show as
"(not readable)".

### Blueprint transfer rules

- Both files of a blueprint are written together (`AtomicFile.WriteAll`), so a failure never leaves half a
  blueprint. The game's `BlueprintManager_Data.json` (its categories) is never touched.
- **Name clashes** (ignoring case) get one question per batch. Ticking "Replace them" overwrites them, keeping
  the existing file's casing and removing a stale `.sbpcfg` if the incoming one has none. Leaving it unticked
  keeps both, and the incoming ones become `Name (2)`. Cancel writes nothing.
- **Imports** read only `.sbp`/`.sbpcfg` entries, each by file name alone (folders in a zip are ignored, so no
  zip-slip). Names the Windows game can't use, files over 64 MB (by bytes actually read), a `.sbpcfg` without
  its `.sbp`, and repeated names are left out and listed.
- Copy and Import refuse while Satisfactory runs; Export works, since it only reads.

## Limits and open questions

- **Dedicated servers:** saves on remote servers aren't shown.
- **Saved-at time zone:** read as UTC; unconfirmed on a non-GMT machine.
- **Visibility 1 = Friends only:** inferred; only 0 (Private) has been seen.
- **Linux paths** (Proton `compatdata`, Heroic `GamesConfig`, native server) are tested with fixtures only.
- **To check in the game:**
  - Does a copied blueprint appear after reloading the session, and in which category?
  - Does a renamed copy (`Name (2)`) show under that name? That assumes the game names blueprints after their files.
- **"Installed" means installed by ADA.** Mods placed by another tool show as *Not installed* until ADA adopts
  them through the Dashboard's found-mods prompt.
