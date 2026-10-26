# `.smmprofile` file format

Status: reading and writing implemented in `ADA.Infrastructure/Profiles/SmmProfileFile.cs`; export in `ProfileExporter.cs`. Clean-room: written only from inspecting an exported file, not from any mod manager's source.

Satisfactory Mod Manager imports **only** a `.smmprofile`, so every exported `.smmprofile` must be complete on its own.

## Sources

| Sample | Where | Observed |
| --- | --- | --- |
| `Mods.smmprofile` (profile "Atria", 233 KB) | `ADA/Docs/.refs/` (not committed) | 2026-10-25 |

## Container

Plain UTF-8 JSON, pretty-printed with 2-space indents. No compression, no base64, no embedded mod files.

## Structure

Three top-level objects:

```json
{
  "profile": {
    "name": "Atria",
    "required_targets": ["Windows"],
    "mods": {
      "AB_CableMod": { "version": ">=0.0.0", "enabled": true },
      "PackagingMatters": { "version": ">=1.2.0", "enabled": true }
    }
  },
  "lockfile": {
    "version": 1,
    "mods": {
      "AB_CableMod": {
        "version": "1.1.9",
        "dependencies": null,
        "targets": {
          "Windows": {
            "link": "https://api.ficsit.app/v1/version/KiPhru2AzwSsT/Windows/download",
            "hash": "68486473…fd27a"
          },
          "WindowsServer": { "link": "…", "hash": "…" },
          "LinuxServer": { "link": "…", "hash": "…" }
        }
      }
    }
  },
  "metadata": { "gameVersion": 513885 }
}
```

### `profile`: what the user asked for

| Field | Type | Sample | Meaning |
| --- | --- | --- | --- |
| `name` | string | `"Atria"` | Profile name |
| `required_targets` | string[] | `["Windows"]` | Platforms every mod must have a build for |
| `mods` | object, keyed by mod reference | 247 entries | The mods the user chose |
| `mods.<ref>.version` | string, semver range | `">=0.0.0"` (246 of 247), `">=1.2.0"` (1) | Accepted versions. `>=0.0.0` means "any" |
| `mods.<ref>.enabled` | bool | all `true` in the sample | Off = kept in the profile but not installed |

`SML` appears here like any other mod (`">=0.0.0"`).

### `lockfile`: exact versions that were resolved

| Field | Type | Sample | Meaning |
| --- | --- | --- | --- |
| `version` | integer | `1` | Lockfile format version; check it on import |
| `mods` | object, keyed by mod reference | 288 entries | Every mod to install, **including dependencies** |
| `mods.<ref>.version` | string, exact semver | `"1.1.9"` (all 288 are `x.y.z`) | The resolved version |
| `mods.<ref>.dependencies` | null in every entry | `null` | Present but unused in the sample |
| `mods.<ref>.targets` | object keyed by platform | `Windows` / `WindowsServer` / `LinuxServer` | Per-platform download |
| `targets.<platform>.link` | string, absolute URL | `https://api.ficsit.app/v1/version/<id>/<platform>/download` | Same path as the API's target `link`, with the host added |
| `targets.<platform>.hash` | string, 64 hex | | SHA-256 of that download. Matches the ficsit.app API's target `hash`, which is the SHA-256 of the file served by the target's download link (checked: WeatherExtended 1.0.0 `Windows`, `1a94ad64…` in both) |

Observations from the sample:

- **41 lockfile mods are not in `profile.mods`** (e.g. `AlkaLib`, `ContentLib`, `DI_Common`). They were pulled in as dependencies. Every profile mod is in the lockfile.
- **`dependencies` is always `null`**, so the file does not say which mod needed which. ADA re-derives that from the API.
- Target sets vary per mod: all three platforms (256), `Windows` only (29), `Windows` + `WindowsServer` (3).
- The one non-default range (`PackagingMatters >=1.2.0`) is locked to `1.2.0`.
- All references are letters, digits and `_`.

### `metadata`

| Field | Type | Sample | Meaning |
| --- | --- | --- | --- |
| `gameVersion` | integer | `513885` | The game changelist (CL) the lockfile was resolved for. Same number as the install's `.version` file `Changelist` |

## Importing into ADA

**A `.smmprofile` is untrusted input** (shared on Discord, downloaded from anywhere). ADA never downloads from a `link` in the file and never trusts its `hash`. It uses each mod's reference and version to look the mod up through the ficsit.app API, and verifies downloads against the API's hash. The file's links and hashes are only compared against the API, as a warning sign if they differ.

Import steps:

1. **Parse leniently, validate strictly.** Reject the file if it is not JSON, a reference is not `[A-Za-z0-9_]+`, a version is not valid semver, or `lockfile.version` is not a known version. Ignore unknown fields (and keep them for export, below).
2. **Show a preview before anything changes:** profile name, mod count, what will be added, updated and removed on the chosen game, and any warnings.
3. **Pick the versions:**
   - **Exact (default when the game build matches):** if `metadata.gameVersion` equals the target game's CL, install the lockfile's versions, so the profile reproduces the same files as on the machine that exported it.
   - **Re-resolve (default when the game build differs):** use `profile.mods` ranges and resolve fresh for the target's CL. The lockfile is then only a hint (prefer its versions where they still fit).
   - Either way, every version is checked against the API: it must still be listed, have a build for the target's platform, and its game-version range must include the target's CL. Failures are listed in the preview, not silently skipped.
4. Mods with `enabled: false` are kept in the ADA profile but not installed.
5. Install through the normal install engine, in dependency order (SML first).

## Exporting from ADA

ADA can export a profile as `.olp`, `.smmprofile`, or both at once (same name, two extensions). Export builds everything first and writes nothing if any mod fails: a version no longer listed on ficsit.app, or no build for the profile's target platform.

Write the same three objects so the file opens in other tools:

- `profile`: the profile's name; every mod with range `>=0.0.0` and `enabled: true` (an `.olp` has exact versions and no enabled flag, and `>=0.0.0` is how other tools write "any"); `required_targets` from the profile's target.
- `lockfile`: `version: 1`; for each installed mod (including dependencies), its version, `dependencies: null`, and its targets with `link` (`https://api.ficsit.app` + the API's target link) and `hash` from the catalogue.
- `metadata.gameVersion`: the target game's CL.
- Unknown fields from an imported file are written back unchanged, so a round trip through ADA does not lose anything.

Formatting matches the sample exactly: 2-space indents, keys sorted by character code (`AutoLink` before `AutomatedInhalers`, not culture-aware sorting), `>` written as-is rather than `\u003E`, and a final newline. **Verified 2026-10-25:** reading the 233 KB sample and writing it back reproduces it byte for byte.

ADA's own format is `.olp`: a profile name and its mods as mod references at exact versions, `{ "name": "Atria", "mods": [ { "name": "SML", "version": "3.12.0" } ] }`, with dependencies listed like any other mod. Converting a `.smmprofile` to `.olp` takes the lockfile's exact versions and leaves out mods whose profile entry has `enabled: false`.

## Open questions

- What a non-null `dependencies` looks like. It may only appear in other versions of the format.
- Whether files exported by other versions use different `lockfile.version` numbers or field names. A second sample from a different manager version would show it.
- Whether `enabled: false` mods also appear in the lockfile. The sample has none disabled.
