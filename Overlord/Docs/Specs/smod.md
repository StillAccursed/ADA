# `.smod` file format

Status: draft, from black-box observation. Clean-room: written from inspecting sample files only, not from any other mod manager's source.

## Sources

| Source | Where | Observed |
| --- | --- | --- |
| `Weather_Extended-1.1.0.smod` | `Overlord/Docs/.refs/` (not committed: third-party binaries) | 2026-10-25 |
| [Manual install directions](https://docs.ficsit.app/satisfactory-modding/latest/ManualInstallDirections.html) | ficsit.app docs (public) | 2026-10-25 |
| [Dedicated server setup](https://docs.ficsit.app/satisfactory-modding/latest/ForUsers/DedicatedServerSetup.html) | ficsit.app docs (public) | 2026-10-25 |
| In-game load test on the reference install (reported by the project owner) | Epic via CrossOver, game 1.2.4.1 | 2026-10-25 |

## Container

- A standard ZIP archive. Every entry in the sample uses Deflate (method 8).
- File name pattern seen: `<Name>-<SemVersion>.smod`. Treat the name as informational only; read identity from the `.uplugin` inside.
- Size: the sample is ~92 MB uncompressed, ~99% of which is two `.pdb` debug-symbol files.

## Layout

Two layouts exist, and Overlord must accept both:

| Layout | Seen in | Archive root |
| --- | --- | --- |
| **Single target (flat)** | Every download from the ficsit.app API (`/v1/version/{id}/{target}/download`), confirmed 2026-10-25 | The mod itself: `<Reference>.uplugin`, `Binaries/`, `Content/`, `Resources/` |
| **Multi-target** | The reference sample in `Docs/.refs` (a locally supplied `.smod`, likely the author's upload) | One folder per platform: `Windows/`, and presumably `WindowsServer/`, `LinuxServer/` |

Detection rule: if a `.uplugin` sits at the archive root, it is flat and the target is the one it was downloaded for. Otherwise, use the top-level folder named after the target platform. An archive with neither is rejected.

The multi-target sample is laid out as follows. Top-level folders are **target platforms**, and each one holds a complete, self-contained copy of the mod for that platform.

```
Windows/
  WeatherExtended.uplugin
  Resources/Icon128.png
  Binaries/Win64/
    FactoryGameEGS-WeatherExtended-Win64-Shipping.dll
    FactoryGameEGS-WeatherExtended-Win64-Shipping.pdb
    FactoryGameEGS-Win64-Shipping.modules
    FactoryGameSteam-WeatherExtended-Win64-Shipping.dll
    FactoryGameSteam-WeatherExtended-Win64-Shipping.pdb
    FactoryGameSteam-Win64-Shipping.modules
  Content/Paks/Windows/
    WeatherExtendedFactoryGame-Windows.pak / .ucas / .utoc
```

Observations:

- The sample only has `Windows/`. Archives built for dedicated servers are expected to add `WindowsServer/` and `LinuxServer/` folders (the platform names the ficsit.app API uses); **unconfirmed** until we have a sample.
- Binaries exist once per store build: `FactoryGameEGS-*` for Epic and `FactoryGameSteam-*` for Steam. Both are present, so one archive serves both stores.
- The `.uplugin` sits at the root of the platform folder, and its file name (`WeatherExtended`) is the mod's reference name.
- A mod with no C++ code would have no `Binaries/` folder; Overlord must not require one.

## `.uplugin` (JSON, Unreal Engine plugin descriptor)

Fields Overlord relies on:

| Field | Sample value | Use |
| --- | --- | --- |
| *(file name)* | `WeatherExtended` | Mod reference; install folder name |
| `SemVersion` | `1.1.0` | Installed version (prefer over `VersionName`) |
| `FriendlyName` | `Weather Extended` | Display name for local (non-catalogue) mods |
| `CreatedBy` | `Casper` | Author for local mods |
| `Plugins[]` | `{ "Name": "SML", "Enabled": true, "SemVersion": "^3.12.0" }` | Dependencies with semver ranges, including the required SML range |
| `GameVersion` | `>=513885` | Compatible game builds, as a range over the changelist (CL) |
| `GameFeature` | `true` | **Install location** (see Installing). Absent means `false` |
| `IsBetaVersion` / `IsExperimentalVersion` | `false` / `false` | Show a badge; never pick these by default when a stable version satisfies the range |
| `CanContainContent` | `true` | Informational |
| `Modules[]` | `{ "Name": "WeatherExtended", "Type": "Runtime" }` | Informational; a mod with no modules is content-only |

Ignored: `FileVersion`, `Version` (integer), `VersionName` (use `SemVersion`), `Category`, URL fields, `Installed`, `BuiltInInitialFeatureState`.

Notes:

- `GameVersion` is compared against the install's changelist, read from the game's own files: `Engine/Binaries/Win64/FactoryGame<Store>-Win64-Shipping.version` → `"Changelist": 513885` (with `"GameVersion": "1.2.4.1"`). This works the same for Steam and Epic. Do not use Steam's `buildid` or Epic's `AppVersionString` for compatibility: those are launcher values.
- `Plugins[].Optional` (a standard Unreal field) may mark optional dependencies; not present in the sample.
- A dependency named `SML` is the mod loader itself, and is resolved like any other mod (see `sml.md`).
- Read with a lenient JSON reader (allow comments and trailing commas): authors sometimes edit `.uplugin` files by hand.

## Installing

Install location, per the ficsit.app manual install directions:

| `GameFeature` in `.uplugin` | Mod folder |
| --- | --- |
| `true` | `<game root>/FactoryGame/Mods/GameFeatures/<Reference>/` |
| `false` or absent | `<game root>/FactoryGame/Mods/<Reference>/` |

- `<Reference>` must be **exactly** the `.uplugin` file name without extension (`WeatherExtended`). The docs stress that folder names and nesting both matter.
- The sample has `"GameFeature": true`, so it installs to `FactoryGame/Mods/GameFeatures/WeatherExtended/WeatherExtended.uplugin`.
- Both locations must be scanned when reading what is installed, and a mod reference must never exist in both at once (treat that as a broken install to repair).

Steps:

1. Verify the download's size and SHA-256 against the API target before opening it.
2. Find the mod root (see Layout): the archive root if flat, otherwise the folder for the target platform (`Windows` for clients, `WindowsServer` / `LinuxServer` for servers). Never copy a client's mods to a server; the docs warn this does not work.
3. Read `GameFeature` from that folder's `.uplugin` and compute the destination above.
4. Extract, verify and commit through the install engine: archive in `Staging/Archives/`, extraction into the hidden `FactoryGame/.overlord/` folder, then a single rename into `Mods` (see `install-engine.md`).
5. Optionally skip `*.pdb` files (debug symbols, not needed to run) to save ~99% of disk space and copy time. Keep them behind a "keep debug symbols" setting, since mod authors ask for them in crash reports.

On servers, refuse to install while the server is running: a running server locks mod files (dedicated server docs).

**Verified in-game (2026-10-25, game 1.2.4.1, CL 513885):** the game loads mods from both `FactoryGame/Mods/` and `FactoryGame/Mods/GameFeatures/`, matching the docs.

## Validation rules

Reject an archive (before staging) if:

- it is not a valid ZIP, or any entry path is absolute or contains `..` (zip-slip);
- it has neither a root `.uplugin` nor a folder for the target platform;
- the mod root has more than one `.uplugin`;
- the `.uplugin` name does not match the mod reference the catalogue says this file belongs to;
- the `.uplugin` is not valid JSON or has no `SemVersion`;
- the uncompressed size exceeds a sanity limit (proposed 2 GB) — guards against zip bombs.
