# `.olp` Overlord profile format

Status: defined 2026-10-25. Implemented in `Overlord.Infrastructure/Profiles/OlpFile.cs`.

Overlord's own profile file: a name and a list of mods at exact versions. Small enough to read, diff and share by hand.

## Schema

```json
{
  "name": "Atria",
  "mods": [
    { "name": "SML", "version": "3.12.0" },
    { "name": "WeatherExtended", "version": "1.1.0" }
  ]
}
```

**Profile**

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `name` | string | yes | Profile name, 1–100 characters |
| `mods` | array of Mod | yes | May be empty |

**Mod**

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `name` | string | yes | The mod **reference** as used by ficsit.app and the `.uplugin` file name (e.g. `WeatherExtended`), not its display name, because display names are not unique. Letters, digits and `_` only |
| `version` | string | yes | The **exact** version, `major.minor.patch` with optional `-prerelease` (e.g. `1.1.0`, `3.12.0-beta.1`) |

## Rules

- **Encoding:** UTF-8 JSON. Field names are lowercase as above. Overlord writes it with 2-space indents, mods sorted by name, characters written as typed (no HTML escaping), and a final newline, so two exports of the same profile are byte-identical and diff cleanly.
- **Exact versions** make a profile reproducible: importing it installs the same files on another machine. Dependencies are listed like any other mod (including SML), so the file is complete without a resolver.
- **Unique names:** a mod may appear once. Matching is case-insensitive (`sml` and `SML` are the same mod).
- **Unknown fields are ignored** on read, so later versions of Overlord can add optional fields without breaking older ones.
- **Limits:** at most 5,000 mods and 1 MB per file; anything larger is rejected as not a real profile.
- **Saved atomically** (temporary file, flush, rename), like every other file Overlord writes.

## Importing

A `.olp` is untrusted input, like a `.smmprofile`. Every mod is looked up on ficsit.app by name and version, and downloads are verified against the API's hash. Before anything changes, the preview lists what will be added, updated and removed, and any mod that cannot be installed: version no longer listed, no build for the target platform, or a game-version range that does not include the target's CL.

## Relation to `.smmprofile`

| | `.olp` | `.smmprofile` |
| --- | --- | --- |
| Versions | Exact only | Ranges (profile) + exact (lockfile) |
| Dependencies | Listed as ordinary mods | Only in the lockfile |
| Download links and hashes | Not stored; always from the API | Stored, but not trusted by Overlord |
| Game build | Not stored | `metadata.gameVersion` |
| Enabled flag | Not stored; a listed mod is installed | `enabled` per mod |

Converting a `.smmprofile` to `.olp` takes its lockfile's exact versions, leaving out mods whose profile entry has `enabled: false`.
