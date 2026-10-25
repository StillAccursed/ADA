# App data layout

Implemented in `Overlord.Platform/Storage/AppPaths.cs`.

## Root

| OS | Root |
| --- | --- |
| Windows | `%LOCALAPPDATA%\Overlord` (`C:\Users\<user>\AppData\Local\Overlord`) |
| Linux | `~/.overlord` |
| macOS | `~/Library/Application Support/Overlord` |

## Folders (all at the root)

| Folder | Contents |
| --- | --- |
| `Databases/` | SQLite databases. `catalog.db` = local copy of the ficsit.app catalogue and cached versions. |
| `Config/` | Settings files. |
| `Staging/` | Downloads and extracted archives waiting to be installed. |

All three are created on launch if missing. The mod catalogue is a cache: deleting `Databases/catalog.db` is always safe, and the next launch downloads it again.

## Staging

`Staging/Archives/<Reference>/<Version>-<Target>.smod` holds downloaded mod archives. Whether they are kept after install is the **Keep downloaded archives** setting (on by default).

Archives are never extracted here. They are extracted into a hidden working folder on the game's own drive (`FactoryGame/.overlord/`), so the final move into `Mods` is an instant rename and nothing is written twice. See `install-engine.md`.

## Settings

`Config/settings.json`, written atomically (to a temporary file, then renamed over the old one), so a crash while saving never leaves a half-written settings file.

| Setting | Default | Meaning |
| --- | --- | --- |
| `keepDownloadedArchives` | `true` | Keep `.smod` files in `Staging/Archives/` after install, for offline repair, rollback and profile switching |
| `downloadRetries` | `5` | Retries after a failed download (network error, wrong size or wrong hash). Range 0–8; out-of-range values are pulled back into range on load |
