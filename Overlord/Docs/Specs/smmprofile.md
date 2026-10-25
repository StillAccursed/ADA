# `.smmprofile` file format

Status: **not started — needs a sample file.**

Clean-room: this spec will be written only from inspecting exported files.

## How to provide a sample

1. In Satisfactory Mod Manager, open the profile menu and choose **Export**.
2. Save the file into `Overlord/Docs/.refs/` (git-ignored).
3. Ideally export two profiles: one small (1–2 mods) and one with mods that have dependencies.

## Questions the sample must answer

- Encoding: plain JSON, or compressed / base64?
- Does it store exact versions, version ranges, or both?
- Does it include the game version or target (client / server)?
- Does it embed mod files, or only references?
- Is there a format version field we can check on import?
