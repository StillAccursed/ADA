# SML (Satisfactory Mod Loader) and game-version matching

Status: draft. Clean-room: written from the public ficsit.app docs and observed files only.

## Sources

| Source | Observed |
| --- | --- |
| [Manual install directions](https://docs.ficsit.app/satisfactory-modding/latest/ManualInstallDirections.html) | 2026-10-25 |
| [Dedicated server setup](https://docs.ficsit.app/satisfactory-modding/latest/ForUsers/DedicatedServerSetup.html) | 2026-10-25 |
| `Weather_Extended-1.1.0.smod` (`.uplugin` declares `"SML": "^3.12.0"`, `"GameVersion": ">=513885"`) | 2026-10-25 |
| Reference install: `Engine/Binaries/Win64/FactoryGameEGS-Win64-Shipping.version` (`"Changelist": 513885`, `"GameVersion": "1.2.4.1"`) | 2026-10-25 |

## Facts

- SML is itself a mod on ficsit.app, reference `SML` ([ficsit.app/mod/SML](https://ficsit.app/mod/SML)).
- It installs to `<game root>/FactoryGame/Mods/SML/`; the folder must be named exactly `SML`. (Older SML versions used a `Mods` folder in the game root; that layout is obsolete.)
- Each SML version is compatible with a range of game changelists (CL). The docs' rule: the installed game's CL must satisfy the version's game-version range.
- Every mod declares the SML range it needs in its `.uplugin` `Plugins[]` entry named `SML`.
- Dedicated servers need SML built for the server's platform, and server support started with SML 3.7.0.
- Installing mismatched versions "is possible but usually won't work" (docs), so Overlord blocks it rather than warning.

## Rules

### 1. One SML per target

Every target has its own SML, chosen for that target: each detected client install, and each remote server. Two installs on one machine (e.g. Steam and Epic, or a client and a server) can need different SML versions, and Overlord never assumes they match.

### 2. Choosing the version

SML is a normal node in the dependency solver, with one extra constraint. A valid SML version must satisfy **all** of:

1. its own game-version range contains the target's CL (from the install's `.version` file, or from the server);
2. every installed or requested mod's `SML` range;
3. it has a build for the target's platform (`Windows`, `WindowsServer` or `LinuxServer`).

Pick the newest stable version that satisfies all three. Prefer the currently installed SML when it still satisfies them, to avoid needless churn.

### 3. When nothing fits

- **No SML release supports the game's CL** (typically right after a game patch): block all mod installs on that target with a plain message: *"No SML release supports game build 513885 (1.2.4.1) yet. Mods stay disabled until SML is updated."* Never install a "closest" version.
- **Mods disagree on SML** (e.g. one needs `^3.11`, another `>=3.12 <3.13`, and no version satisfies both): show the solver's explanation naming both mods, and offer to pin or remove one.

### 4. Lifecycle

- SML is installed automatically, in the same transaction as the first mod that needs it, and never as a separate step that can be left half-done.
- When the last mod is removed, offer to remove SML too (SML alone is harmless, so don't force it).
- On every launch and rescan, compare each target's CL with the CL recorded in its lockfile. If the game has updated:
  - re-run the solver for that target;
  - flag mods with no compatible version;
  - offer the SML and mod updates the new CL needs, as one transaction.
- An SML that Overlord did not install (another manager, or a manual install) is detected by reading `Mods/SML/SML.uplugin` → `SemVersion`, and adopted into the lockfile if it satisfies the rules; otherwise it is replaced in the next transaction, after the user confirms.

### 5. Client and server

- A server's SML is chosen from the server's CL and platform, not the client's.
- When syncing a client profile to a server, mods must resolve to the **same mod versions** on both sides (the docs say mismatched versions can prevent joining). SML may differ only if both versions satisfy every mod's range; show a warning when they differ.
- Mod config files are not synced in v1. The docs say servers have no remote config interface and configs must be copied from a client; consider a "copy configs to server" action later.

## Lockfile fields (per target)

```json
{
  "target": "epic:crossover:Epic Games Store (Intel)",
  "gameChangelist": 513885,
  "gameVersion": "1.2.4.1",
  "platform": "Windows",
  "sml": "3.12.0",
  "mods": { "WeatherExtended": { "version": "1.1.0", "gameFeature": true } }
}
```

Recording `gameFeature` per mod lets removal find the folder without re-reading an archive.

## Open questions

- Confirm `SML.uplugin` has no `GameFeature: true` (expected: installs directly under `Mods/SML`).
- Where a dedicated server stores its `.version` file on Linux (`Engine/Binaries/Linux/`?), and its file name. Needs a server sample.
- How the ficsit.app API exposes each SML version's game-version range (field name and format). Check when building the API client.
