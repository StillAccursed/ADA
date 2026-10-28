# Dashboard

The dashboard has five cards. At wide sizes, Installations and Disk Status occupy the left column, Latest Updated Mods occupies the centre, and Profiles and ADA Status occupy the right. Cards reflow into two columns and then one column as the available page width decreases or inherited text size increases. The whole page scrolls vertically.

## Installations

The dropdown selects among installations ADA detected through launcher records. It does not allow typed paths or manual installation creation. Selection is shared with the Mods page, and rescanning retains the same installation by its game path while it still exists.

The card shows build, host, location and managed-mod state. **Details and actions** contains repair, import and missing-dependency actions. Actionable conditions expand those details. The game-running banner and existing close-game confirmation remain above the cards.

## Latest Updated Mods

Recent releases come from the local catalogue, ordered by release date. Entries without a release date are omitted. Metadata edits do not move a mod to the top. Names, descriptions and available stable-version summaries come from cached catalogue rows, without a version-history request per card row.

Change the count under **Settings → Dashboard → Latest updated mods**. The default is five; supported values are one through twenty. The count is saved automatically and updates the dashboard after a successful save. Existing settings files use the default.

The card refreshes when entering the dashboard, when its setting changes and after the existing catalogue sync. It does not add network polling. Failed refreshes retain the last good list and show a notification.

## Disk Status

- **Mods folder size** measures the selected installation's `FactoryGame/Mods`, including nested GameFeatures and unmanaged files.
- **Archive size** measures downloaded `.smod` files in ADA's shared archive cache. Archives belong to the application and are shared across installations.
- **Backups size** is reserved for the future system of user-created backups of Saves, Settings and Mods. It displays **Coming soon**. Installer recovery copies are not user backups. Backup creation, restoration and retention are not implemented by this dashboard change.
- **Mods + archives** sums the two available measurements. It excludes the future backup category.
- **Available** and **Total capacity** refer to the selected installation's volume, rather than the sum of tracked folders. A separate archive volume gets its own free-space readout.

Measurements run off the UI thread. Changes to selection or data supersede stale scans, and bursty refresh requests are coalesced. The dashboard refreshes after operations and archive deletion; the process watcher does not walk directories on each tick. Directory links are not traversed. Missing folders are empty, while unreadable or partially measured folders show Unknown or Partial. An incomplete measurement cannot produce a seemingly exact combined total.

## Profiles

Active-profile selection applies to the installation selected in the Installations card. The Mods page's installation selector remains available; it is not an active-profile selector.

Choosing another profile applies its exact saved versions, resolves required dependencies for the captured game build/platform, removes managed mods outside the result and leaves unrelated unmanaged files alone. Missing/incompatible versions and unmanaged collisions fail without silently choosing newer versions. The implicit Default profile is not written merely by viewing the dashboard; an explicit switch away saves it if needed.

Selection is unavailable while the game is running, a check/switch is active, or mod requests for that installation are queued. The service also reserves the installation across planning, downloads, installation and activation, and the installer retains its cross-process file lock.

A profile switch is committed as one transaction. An interrupted, uncommitted switch rolls back as a whole. A committed switch durably records pending profile activation before cleanup. Activation writes the active-profile name and requested-mod state before removing the marker. If activation fails, the marker remains and subsequent changes are blocked until reconciliation. Startup reconciliation precedes recording installed mods so the former profile cannot be overwritten with the target's mods. Ordinary non-profile transaction recovery retains its existing per-mod behaviour and commit-before-cleanup order.

## ADA Status

The timestamp is the last **catalogue** check, shown in local time. It is not an ADA application-update check. Notifications reflect current catalogue errors, installation notices, repairs, missing dependencies, unmanaged-mod decisions, available mod updates and profile-switch state. Repeated conditions are deduplicated. This is a current status list, not a persistent unread-notification/history service.
