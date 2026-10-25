# ficsit.app API

Status: in use by `Overlord.Infrastructure/Ficsit/FicsitApiClient.cs`. Observed 2026-10-25 by schema introspection and live queries.

## Endpoint and terms

- GraphQL: `POST https://api.ficsit.app/v2/query` with `{"query": "..."}`.
- Downloads: `https://api.ficsit.app` + a target's `link`, e.g. `/v1/version/{versionId}/{Windows|WindowsServer|LinuxServer}/download`.
- Terms: the API "may not be used in any application that is closed-source", and must be used "reasonably and as intended". Overlord is MIT-licensed and sends `User-Agent: Overlord/<version> (+https://github.com/StillAccursed/Overlord)`.

## Fields Overlord uses

**Mod:** `id`, `mod_reference`, `name`, `short_description`, `full_description` (Markdown), `logo`, `downloads`, `popularity`, `hotness`, `created_at`, `updated_at`, `last_version_date`, `tags { name }`, `authors { user { username } }`, `compatibility { EA { state } EXP { state } }` (`Works` / `Damaged` / `Broken`, may be null).

**Version:** `id`, `version`, `game_version` (CL range, e.g. `>=513885`), `stability` (`release` / `beta` / `alpha`), `required_on_remote`, `created_at`, `targets { targetName link size hash }`, `dependencies { mod_reference condition optional }`.

There is no gallery field. Images appear only inside `full_description`, which is why image search indexes alt text and file names from the Markdown.

## Behaviour to know about

| Finding | Consequence |
| --- | --- |
| **`updated_at` is rewritten on every mod routinely.** All 1,644 mods had an `updated_at` within the last day, and pages held hundreds of identical timestamps. | Never use it for change detection or for paging. Full syncs page by `created_at asc` (stable), incremental syncs by `last_version_date desc`. Cached versions are invalidated only when `last_version_date` changes. |
| Paging by a column with many ties returns duplicates and skips mods. Seen live: 1,644 rows read, 1,635 distinct. | A full sync only deletes local mods when it saw as many distinct mods as the reported `count`. |
| Requesting `sml_version` on the SML mod's own versions returns an error ("could not find SML version") and nulls the whole mod. | Do not request `sml_version`; the SML range is in `dependencies` (`mod_reference: "SML"`). |
| `logo` may be an empty string. | Treated as no logo. |
| SML versions list `game_version` like any mod (3.12.0: `>=491125`), and have builds for all three targets. | SML is resolved as a normal dependency plus the CL rule (see `sml.md`). |
| A full catalogue (1,644 mods) takes 17 requests of 100, ~6 s, ~14 MB in SQLite. | Daily full sync is cheap enough; incremental is one request when nothing was released. |

## Downloads and `hash` (confirmed 2026-10-25)

- A target's `hash` is the **SHA-256 of the file served by its `link`**, and `size` is its exact byte count. Verified by downloading WeatherExtended 1.1.0 `Windows` (`/v1/version/3tVQp8kCrZ8FaH/Windows/download`, one redirect to file storage): 22,051,177 bytes, SHA-256 `0e8780ed…5d9e`, both matching the API.
- The install engine must check both before extracting, and discard the file on any mismatch.
- **Per-target downloads are flat:** the `.uplugin`, `Binaries/`, `Content/` and `Resources/` sit at the archive root, with no `Windows/` folder. See `smod.md`, Layout.
- The earlier reference sample (`Weather_Extended-1.1.0.smod`, 21,576,445 bytes, SHA-256 `69be9399…`) is a different file in the multi-target layout. It was not obtained from a target `link`.
