# ADA identity and data locations

Approved and implemented on 26 October 2026.

## Application identity

The solution is `ADA.sln`. Projects, assemblies and root namespaces are `ADA`, `ADA.Core`, `ADA.Platform`, `ADA.Infrastructure`, and `ADA.Tests`.

Application namespaces, Avalonia class/resource references, friend assemblies, project references, CI, branding, messages, logging, tests, documentation, local notes and IDE mappings use ADA. Existing feature behavior, project GUIDs, packages and file formats are retained.

The Git remote is `https://github.com/StillAccursed/ADA.git`.

## Data locations

| Platform | Application data root |
| --- | --- |
| Windows | `%LOCALAPPDATA%\ADA` |
| macOS | `~/Library/Application Support/ADA` |
| Linux | `~/.ada` |

Each game uses `FactoryGame/.ada` for ownership records, the lock and transactional staging. New daily logs use `ada-<date>.log`. Profile formats and contents, archive hashes, database contents and mod identifiers retain their existing contracts.

Existing installations require a one-time offline data cutover before the first ADA launch. Close every application instance and the game, preserve data and recovery copies, resolve pending recovery, refuse destination conflicts, and move complete roots together. Do not run superseded binaries after cutover. No automatic compatibility fallback is shipped.

## Preserved features

- Direct install, uninstall, queued changes and list/grid/card quick actions, retaining dependency checks, captured game identity and game-running checks.
- Separate Stable/Experimental compatibility reports, tooltips and accessible descriptions.
- Formatted Markdown/HTML, protected code examples, separate image gallery and disabled description hyperlinks.
- The Markdown dependency, third-party notices and build/publish copy declarations.
- Current Wine drive mapping, theme brushes, project resources and existing ADA artwork outputs.

## Validation

Verified from the final ADA workspace on 26 October 2026:

- Release build: zero warnings and errors.
- Test suite: 338 passed, 0 failed.
- Framework-dependent publish succeeded; build and actual publish output both contain the notices file unchanged from the source copy.
- All five projects evaluate their explicit ADA assembly and namespace identities correctly.
- Existing source behavior was preserved: 144 files passed identity-only or byte-preservation comparison against the source snapshot; approved path/API class edits were checked separately.
- macOS app-data cutover: 12 files preserved/translated as planned, including the catalog database and six archive payloads with verified hashes.
- Game working-folder cutover: three ownership/lock files preserved; no pending journal or staging existed.
- Disposable cutover checks passed for preservation, repeated execution, conflicts and pending-recovery refusal.
- All 12 existing artwork outputs were preserved byte-for-byte.
- Git access confirmed the ADA repository and retained its existing history. The patch is local and has not been committed or pushed.

Final workspace: `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/ADA`.
Repository: `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/ADA/ADA`.

Source, runtime and generated-output recovery copies, manifests and validation logs are retained in `/private/tmp/ada-rename-20261026`, outside the active tree. Immutable Git history is retained. Codex's saved project location must be reopened at the new workspace path because automated control of the Codex UI is unavailable.

Interactive UI checks and the final reference audit are recorded after they complete. macOS evidence does not establish Windows/Linux runtime behavior or gameplay.
