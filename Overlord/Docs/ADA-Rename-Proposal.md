# Proposal: replace ALL Overlord references with ADA

Created: 25 October 2026. Rescanned and revised: **26 October 2026**. Status: **proposed; awaiting explicit approval before implementation**.

## Intended outcome and scope

Replace **every case-insensitive reference to `Overlord` in the active application, repository, local project material, and application-owned runtime locations** with the appropriate ADA spelling. This includes `Overlord`, `OVERLORD`, and `overlord` in identifiers, filenames, directories, text, URLs, logs, fixtures, generated output, and local configuration.

**All workstreams in this proposal are required parts of one complete rename.** Branding, runtime-folder cutover, ignored local notes, GitHub repository references, and the enclosing checkout/workspace folder names are included. No part is optional. The resulting projects are `ADA`, `ADA.Core`, `ADA.Platform`, `ADA.Infrastructure`, and `ADA.Tests`.

Use `ADA` for product names, namespaces, assemblies, project names, and conventional application folders; `ada` for lowercase hidden folders, log names, and temporary prefixes. Preserve namespace suffixes, application behavior, user data, and file-format contracts while changing all references to the old product identity.

This is a revised proposal, not approval to implement it. Existing Git commits and this conversation retain their historical contents; rewriting Git history is a separate operation from replacing every reference in the current project. During review this proposal necessarily contains before/after mappings. At completion, replace those mappings and the old-name inventories with an ADA-only implementation record so the proposal itself does not defeat the final zero-reference check.

## Inspection evidence: 26 October 2026

Repository: `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/Overlord/Overlord`. Enclosing workspace: `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/Overlord`. The nested repository, application folder, and workspace remain distinct locations.

HEAD remains `8414fda` (`Fixed Updating`). The working tree now has **13 modified tracked files and four untracked files**, including this proposal. This revision reads the current working files, not the committed versions. Preserve all existing changes; Appendix E records the snapshot and newly added files.

| Inventory | Current rescan |
| --- | ---: |
| Tracked repository files | 141 |
| Untracked files, including this proposal | 4 |
| Current tracked + untracked repository files | 145 |
| C# files, including two new untracked files | **114** |
| Root namespace declarations to rename | **114** |
| Rooted C# using directives to rename | **227** |
| Fully qualified type references outside using/namespace declarations | **3** |
| Project files / project references / friend-assembly entries | **5 / 9 / 2** |
| XAML files / files containing old namespace references | **9 / 8** |
| Ignored maintained notes / reference samples | **7 / 2** |
| Ignored IDE files / generated build files | **7 / 947** |
| Workspace artwork outputs outside the repository | **12** |

The rescan included all current tracked and untracked C# files, project/solution/build configuration, XAML, README, notices, five existing tracked Docs files, seven ignored notes, reference samples, IDE state, generated-output inventory, and the workspace `output/imagegen` directory. The maintained text scan found 144 files containing the old name, with 669 literal case-insensitive occurrences, excluding this proposal and generated/IDE content. These numbers are a dated snapshot, not permanent acceptance limits.

The reference `.smod` ZIP has 11 entries; searching entry names and decompressed bytes found no case-insensitive old-name match. The sample `.smmprofile` and placeholder `.ico` also have no raw literal match. All six artwork prompts contain no old-name reference; their six PNG filenames already use ADA. This was a reference/byte inventory, not visual acceptance of the artwork or a decision to install a particular logo.

Read-only MSBuild evaluation with SDK `10.0.402` reconfirmed each project's current `AssemblyName` and `RootNamespace` still equals its corresponding `Overlord*` project name. The configured local `origin` still points to `https://github.com/StillAccursed/Overlord.git`; the hosted repository's live rename status was not queried. Do not infer hosted status solely from the local remote.

No compilation, test execution, application launch, runtime-data inspection/migration, hosted mutation, or rename was performed for this rescan. Prior validation notes are historical evidence, not proof that this revised rename works.

## Changes since the previous scan that this proposal must preserve

| Current addition/change | Rename implications and behavior to preserve |
| --- | --- |
| `ViewModels/BranchCompatibilityViewModel.cs` and its new test file | Add both to the rename inventory. Preserve separate Stable/Experimental reports, ✓ only for Works, and descriptions used by tooltips/assistive technology. Selection of an older mod version must not change current branch reports. |
| `Markdown.Avalonia.Html` package and formatted descriptions | Regenerate the complete proposed app project from its current contents, retaining the new package. Keep its central `12.0.0-a3` version; do not remove it by applying the old project example. |
| `THIRD-PARTY-NOTICES.md` and output/publish content item | Rename the single product-name occurrence in the introductory sentence; preserve third-party copyright/licence text and source links. Retain `..\THIRD-PARTY-NOTICES.md`, `Link`, and both `PreserveNewest` copy attributes. |
| Description preparation, renderer and tests | Keep Markdown/structural HTML, code-example preservation, plain-text changelogs, the separate gallery, short-description fallback, removal of author CSS/scripts/comments/images/attributes, and the disabled description hyperlink command. |
| One-click changes and quick action buttons | Keep direct apply after successful planning, clicked-mod identity, latest-compatible quick install, dependency-aware uninstall, selected-version install/switch, per-game queues, captured original target during asynchronous planning, and late game-running checks. `PlanPanel` remains progress/outcomes; do not reintroduce confirmation state. |
| `FailureBrush` and compatibility bindings | Move current theme content intact, including both dark/light failure brushes, status colors, tooltips and accessibility descriptions. Preserve existing layout/styles and compiled bindings. |
| `WinePrefix` path change | Retain the current `Path.Join` handling for `dosdevices` and its case-insensitive host resolution. Only the root namespace changes. |
| Six ADA logo images and six prompts beside the repository | Preserve them byte-for-byte and move them with the enclosing workspace. They are not currently project resources; the existing window icon is still the Avalonia placeholder. Selecting/converting/installing a logo is not implied by the reference rename. |

The rename must operate on these current files, including untracked additions. No feature implementation is proposed beyond replacing the application identity and runtime roots already described.

## Workstream A: namespaces, projects, source folders, and build integration

### 1. Project, solution, and source-folder mapping

Paths in this table are relative to the **current repository root**, rather than the enclosing workspace.

| Current path | Proposed path |
| --- | --- |
| `Overlord.sln` | `ADA.sln` |
| `Overlord/` | `ADA/` |
| `Overlord/Overlord.csproj` | `ADA/ADA.csproj` |
| `Overlord.Core/` | `ADA.Core/` |
| `Overlord.Core/Overlord.Core.csproj` | `ADA.Core/ADA.Core.csproj` |
| `Overlord.Platform/` | `ADA.Platform/` |
| `Overlord.Platform/Overlord.Platform.csproj` | `ADA.Platform/ADA.Platform.csproj` |
| `Overlord.Infrastructure/` | `ADA.Infrastructure/` |
| `Overlord.Infrastructure/Overlord.Infrastructure.csproj` | `ADA.Infrastructure/ADA.Infrastructure.csproj` |
| `Overlord.Tests/` | `ADA.Tests/` |
| `Overlord.Tests/Overlord.Tests.csproj` | `ADA.Tests/ADA.Tests.csproj` |

All child folders move with their project, including `Views`, `ViewModels`, `Assets`, `Themes`, `Docs`, `Catalog`, `Installing`, and test fixture folders. Their existing names and relative arrangement are retained. Individual `.cs` class filenames do not contain `Overlord` and do not need renaming.

Resulting repository structure:

```text
<repository root>/
  ADA.sln
  Directory.Build.props
  Directory.Packages.props
  README.md
  .github/workflows/ci.yml
  ADA/
    ADA.csproj
    App.axaml
    App.axaml.cs
    Program.cs
    Services.cs
    Assets/
    Docs/
    Imaging/
    Themes/
    ViewModels/
    Views/
  ADA.Core/ADA.Core.csproj
  ADA.Platform/ADA.Platform.csproj
  ADA.Infrastructure/ADA.Infrastructure.csproj
  ADA.Tests/ADA.Tests.csproj
```

### 2. Namespace declarations and C# references

Change the leading identifier only: `Overlord` → `ADA`, and `Overlord.<suffix>` → `ADA.<suffix>`. Preserve type names, namespace suffixes, event wiring, dependency boundaries, and control flow except for the explicit runtime-root cutover, product text changes, and touched-constant naming described below.

| Project | C# files | Namespace families after rename |
| --- | ---: | --- |
| Desktop application | 24 | `ADA`, `ADA.Imaging`, `ADA.ViewModels`, `ADA.Views` |
| Core | 13 | `ADA.Core.Catalog`, `.Games`, `.Profiles`, `.Resolution`, `.Settings` |
| Infrastructure | 27 | `ADA.Infrastructure.Catalog`, `.Downloads`, `.Ficsit`, `.IO`, `.Installing`, `.Net`, `.Profiles`, `.Settings`, `.Threading` |
| Platform | 13 | `ADA.Platform.Detection`, `.Formats`, `.Launchers`, `.Paths`, `.Storage` |
| Tests | 37 | `ADA.Tests.Catalog`, `.Core`, `.Detection`, `.Downloads`, `.Ficsit`, `.Fixtures`, `.Formats`, `.Installing`, `.Paths`, `.Profiles`, `.Resolution`, `.Settings`, `.Storage`, `.ViewModels` |
| **Total** | **114** | All declarations and their consumers |

The rescan found **227 `using` directives rooted in Overlord**, including `using static` fixture imports. Update every directive, including those in the two new files. A namespace/using-only replacement would still miss these **three fully qualified type references**:

- `Overlord.Tests/Catalog/ModDescriptionTests.cs`: `Overlord.ViewModels.ModItemViewModel` and `Overlord.Tests.Fixtures.CatalogFixtures` in the new short-description fallback test.
- `Overlord.Tests/ViewModels/FixAfterImportTests.cs`: `Overlord.Platform.Detection.GameProcess` in the generic argument.

Update all three to their `ADA` equivalents without changing the tested behavior.

Partial source-generated types must retain matching namespaces across their declarations: Avalonia views, CommunityToolkit view models, System.Text.Json contexts, and Dapper-generated mapping/interceptor consumers must compile under the new project identities. Regenerate generated output through the build; do not patch generated files.

Complete representative classes after the namespace rename:

```csharp
namespace ADA.ViewModels;

public sealed class PlaceholderViewModel(string title, string description) : ViewModelBase
{
    public string Title { get; } = title;

    public string Description { get; } = description;
}
```

```csharp
using Avalonia.Controls;

namespace ADA.Views;

public partial class PlaceholderView : UserControl
{
    public PlaceholderView()
    {
        InitializeComponent();
    }
}
```

These examples preserve their complete existing implementation. This rename does not reformat unrelated code or add documentation that merely restates a member's name. Any newly introduced non-obvious member would follow the supplied AGENTS.md naming, access, documentation, ordering, and brace rules.

Complete newly added branch compatibility class after its identity-only rename:

```csharp
using ADA.Core.Catalog;

namespace ADA.ViewModels;

/// <summary>Displays a Ficsit branch report as a binary compatibility indicator.</summary>
public sealed class BranchCompatibilityViewModel(CompatibilityState state)
{
    /// <summary>Only a Works report confirms compatibility; missing reports do not.</summary>
    public bool IsCompatible => state == CompatibilityState.Works;

    public string Glyph => IsCompatible ? "✓" : "✕";

    /// <summary>Explains the indicator for tooltips and assistive technology.</summary>
    public string Description => state switch
    {
        CompatibilityState.Works => "Compatible",
        CompatibilityState.Damaged => "Partly broken",
        CompatibilityState.Broken => "Not compatible",
        _ => "Compatibility not reported",
    };
}
```

### 3. Explicit project identities and project references

Set both `RootNamespace` and `AssemblyName` explicitly in every renamed `.csproj`. Renaming the project already changes the evaluated defaults; explicit values make the intended namespace and output identity reviewable in each project file.

| Project | `RootNamespace` | `AssemblyName` | Project dependencies |
| --- | --- | --- | --- |
| `ADA` | `ADA` | `ADA` | Core, Platform, Infrastructure |
| `ADA.Core` | `ADA.Core` | `ADA.Core` | None |
| `ADA.Platform` | `ADA.Platform` | `ADA.Platform` | Core |
| `ADA.Infrastructure` | `ADA.Infrastructure` | `ADA.Infrastructure` | Core |
| `ADA.Tests` | `ADA.Tests` | `ADA.Tests` | Core, Platform, Infrastructure, desktop application |

Update all **nine** `ProjectReference` paths and both `InternalsVisibleTo` entries. The latter occur in the desktop and Infrastructure projects and must target `ADA.Tests`, the new test assembly name. Otherwise tests lose access to internal members even when namespaces are correct.

Appendix A gives the complete proposed contents of all five project files. Preserve package references, central versions, `net10.0`, nullable/implicit-using settings, Release warnings-as-errors, diagnostics conditions, manifest/resource declarations, the Markdown package, notices-copy content item, Dapper interceptor configuration, and test packaging settings.

`Directory.Build.props` and `Directory.Packages.props` contain no Overlord-specific identity and need no content change. Solution project display names and relative paths change together; retain existing project GUIDs and configuration mappings.

Expected assembly filenames become `ADA.dll`, `ADA.Core.dll`, `ADA.Platform.dll`, `ADA.Infrastructure.dll`, and `ADA.Tests.dll`. The application host and associated `.deps.json`, `.runtimeconfig.json`, debug symbols, and generated assembly metadata follow the new project/assembly identities. External tools that launch the previous assembly or executable need their paths updated.

### 4. Avalonia references

Update the eight affected `.axaml` files alongside their C# code-behind declarations:

- `Overlord/App.axaml`
- `Overlord/Views/DashboardView.axaml`
- `Overlord/Views/MainWindow.axaml`
- `Overlord/Views/ModsView.axaml`
- `Overlord/Views/PlaceholderView.axaml`
- `Overlord/Views/PlanPanel.axaml`
- `Overlord/Views/SettingsView.axaml`
- `Overlord/Views/UpdatesWindow.axaml`

Change `x:Class="Overlord.…"`, `xmlns:vm="using:Overlord.ViewModels"`, and `xmlns:views="using:Overlord.Views"` to their `ADA` equivalents. Preserve the existing aliases, so bindings such as `x:DataType="vm:MainViewModel"` remain valid.

Change the merged theme reference in `App.axaml` to `avares://ADA/Themes/Tokens.axaml`. The assembly component of that URI must match the new `AssemblyName`. `Themes/Tokens.axaml` itself has no Overlord reference and only moves with the project. The relative icon path `/Assets/avalonia-logo.ico` stays valid. Preserve the newly added third-party namespaces `clr-namespace:Markdown.Avalonia;assembly=Markdown.Avalonia` and `clr-namespace:Markdown.Avalonia.Html;assembly=Markdown.Avalonia.Html` exactly: they refer to dependency assemblies. Keep `DescriptionViewer`, its plugin configuration and disabled hyperlink command, `$parent[views:ModsView]` quick-action command bindings, the branch-report bindings, and all current theme brushes. Only application namespace/class/resource identity references change.

Complete matching view markup after renaming:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:ADA.ViewModels"
             x:Class="ADA.Views.PlaceholderView"
             x:DataType="vm:PlaceholderViewModel">
    <StackPanel Margin="32,28" Spacing="8">
        <TextBlock Text="{Binding Title}" FontSize="24" FontWeight="SemiBold"
                   Foreground="{DynamicResource TextPrimaryBrush}" />
        <TextBlock Text="{Binding Description}" TextWrapping="Wrap"
                   Foreground="{DynamicResource TextSecondaryBrush}" />
    </StackPanel>
</UserControl>
```

Update the Windows manifest's `<assemblyIdentity version="1.0.0.0" name="Overlord"/>` to `name="ADA"`. Keep its version, supported OS identifier, and compatibility declarations.

### 5. CI, ignore rules, and documentation references

In `.github/workflows/ci.yml`, replace `Overlord.sln` with `ADA.sln` in restore, Release build, and test commands. Preserve the Windows/Linux/macOS matrix and artifact upload configuration.

In `.gitignore`, update these application-relative entries:

```text
ADA/Docs/.proposals
ADA/.claude
ADA/Docs/.refs
ADA/bin
ADA/obj
```

Retain the remaining ignore rules. The general `.claude/` rule already covers the ignored `Docs/.claude` notes after the directory move; the general `bin/` and `obj/` rules still apply to every project.

Update README build/run commands and its project table. Proposed commands, run from the repository root:

```sh
dotnet build ADA.sln
dotnet test ADA.sln
dotnet run --project ADA/ADA.csproj
```

Update source/project paths and relative links in the five existing tracked Docs files: `Development-Proposal.md`, `Fix-Behaviour-Review.md`, `Transaction-Cleanup.md`, `Specs/smod.md`, and `Specs/smmprofile.md`. For example, `../../Overlord.Infrastructure/Installing/ModInstaller.cs` becomes `../../ADA.Infrastructure/Installing/ModInstaller.cs`. Preserve historical dates, approval records, behavioral findings, and validation claims. Include the new root `THIRD-PARTY-NOTICES.md`: replace its single product-name mention, retain all third-party notices verbatim, and keep its project copy-to-output/publish wiring. References to old feature limitations in prior reviews remain historical findings; do not mistake them for the current implementation or reintroduce removed confirmation state.

Move the seven ignored local Markdown notes with the application directory and replace every product-name, namespace, source-path, URL, log-name, and runtime-path reference in them. Include historical prose: preserve its dates and technical meaning while translating the product identity to ADA. Keep these local notes ignored. The two ignored `.refs` samples currently have no raw case-insensitive product-name match; the ZIP sample also has no matching entry name. Inspect their extracted text/metadata during the final audit. Preserve third-party payloads that contain no product reference, and keep both samples ignored.

Rename the hosted GitHub repository to `ADA` as part of Workstream D, then replace the old URL in Git remotes, README badge/link targets, API identification, and all documentation with `https://github.com/StillAccursed/ADA`. Do not leave links relying on the old repository name or its redirect. Verify access and destination-name availability before the hosted rename; if that step is unavailable, report the rename as incomplete rather than retaining the old URL as an accepted exception.

### 6. Local checkout and enclosing workspace folders

The five project-folder renames above are tracked changes. The checkout directory and its enclosing workspace are separate local folder names. To cover every source-container folder named Overlord while retaining the current layout, the final local paths would be:

| Current absolute directory | Proposed absolute directory |
| --- | --- |
| `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/Overlord` | `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/ADA` |
| `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/Overlord/Overlord` | `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/ADA/ADA` |
| `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/Overlord/Overlord/Overlord` | `/Users/ryanmacham/Documents/Coding/Modding/Satisfactory/ADA/ADA/ADA` |

The repeated ADA levels follow the existing three Overlord levels; collapsing those levels would be an additional layout change. Core, Platform, Infrastructure, and Tests would be siblings of the innermost `ADA` application folder.

Perform local container moves last, after validating the tracked rename. First confirm the target directories do not exist, preserve the checkout's `.git` and ignored local assets, stop any process tied to the old directory, then reopen the solution and update the Codex/IDE workspace location. Move the sibling `output/imagegen` directory with the enclosing workspace; preserve its 12 current ADA files. Update the remote to the renamed hosted repository. Re-evaluate projects from the final path to catch absolute local-path assumptions.

The outermost move writes to the parent `Satisfactory` directory, outside this session's current writable workspace. It requires filesystem approval or a user-performed move at implementation time. This proposal requests no move now and makes no claim that a Git patch can encode these local container renames.

## Workstream B: every product-name string and local document

Apply these changes throughout the current tree, including comments, XML summaries, test data, and ignored local notes. The listed files identify known occurrences; the final case-insensitive search also covers newly edited or added files.

| Area | Required change |
| --- | --- |
| Window title and navigation header | `Views/MainWindow.axaml`: `Title="ADA"`, header `Text="ADA"` |
| Dashboard explanation | `Views/DashboardView.axaml`: replace every product-name mention |
| View-model messages | `ViewModels/DashboardViewModel.cs`, `GameInstallViewModel.cs`, `ModsViewModel.cs`: ADA throughout |
| Installer, API, and platform messages | `Infrastructure/Installing/ModInstaller.cs`, `ModResolutionService.cs`, `Infrastructure/Ficsit/FicsitApiClient.cs`, `Platform/Detection/GameDetection.cs`, `Platform/Storage/AppPaths.cs`: ADA throughout |
| Startup messages and log prefix | `App.axaml.cs`: ADA startup/failure messages and `ada-.log` |
| API identification | `Infrastructure/Ficsit/FicsitApi.cs`: `ADA/0.1 (+https://github.com/StillAccursed/ADA)` |
| Temporary test folders | `Tests/Fixtures/TempDir.cs`: `ada-tests-` |
| Test assertions, comments, and example names | Update installer-message assertions and every product-name occurrence in fixtures, comments, process-path examples, JSON notes, and test descriptions |
| README, five tracked Docs files, and new notices file | Replace every product-name mention, source path, runtime path, URL, and heading; preserve behavioral claims, dates, and approval/validation history |
| Seven ignored Markdown notes | Apply the same complete replacement; retain their ignored status |
| This proposal | After implementation, replace the before/after proposal with an ADA-only completion record, retaining the implementation outcome and actual validation evidence |

The notices change concerns the product-name introduction only; it does not rewrite third-party attribution or licence terms. The table abbreviates renamed project prefixes; Appendix B lists the current full paths. Keep `.olp`, `.smmprofile`, `.smod`, their JSON contracts, the ficsit.app endpoints, mod identifiers, and third-party names: none is a literal reference to the old product name. New artwork is unnecessary to complete the text/reference rename; the existing icon has no literal old-name match.

## Workstream C: runtime folders and existing data

Rename the application-owned runtime locations too. There will be no legacy-name fallback in the finished ADA code or documentation.

| Existing runtime location | Required new location | Owner |
| --- | --- | --- |
| `%LOCALAPPDATA%\Overlord` | `%LOCALAPPDATA%\ADA` | `Overlord.Platform/Storage/AppPaths.cs` |
| `~/Library/Application Support/Overlord` | `~/Library/Application Support/ADA` | `AppPaths` |
| `~/.overlord` | `~/.ada` | `AppPaths` |
| `<game>/FactoryGame/.overlord` | `<game>/FactoryGame/.ada` | `Overlord.Infrastructure/Installing/GameModFolders.cs` |
| `Logs/overlord-<date>.log` | `Logs/ada-<date>.log` | `Overlord/App.axaml.cs` and existing application log files |

Update `AppPaths`, `GameModFolders`, their summaries, `AppPathsTests`, `Installing/GameFolder.cs`, and all path references in docs and local notes together. The game-root move includes `installed.json`, `requested.json`, `journal.json` when present, `lock`, and every transaction staging/backup folder; it cannot be replaced by creating a fresh empty `.ada` folder.

Use a **one-time offline cutover before the first ADA launch**, rather than retaining old-name migration strings in the shipped application. The old locations are inputs to this reviewed cutover procedure only. Temporary migration material and recovery copies stay outside the finished active tree and are reported separately; they must not leave old-name references in the released code or current documentation.

1. Inventory the actual app-data roots and detected game working folders, their contents, and any existing destinations. Record a manifest and preserve recovery copies before moves. If both old and new roots exist, stop that particular move and review the concrete conflict; never overwrite or silently merge.
2. Close every old application instance and prevent it from being relaunched during or after cutover. Require the game to be closed before changing its working directory. Check the current lock; after relocation, the old binary would otherwise use a different lock and could operate concurrently.
3. Resolve pending install recovery using the existing engine before moving the game working folder. If recovery cannot finish, do not create a new working directory or let ADA treat the game as unmanaged. Keep all journal/staged/backup data together until it can be safely recovered.
4. Move the full app-data root to its new name, retaining settings, favourites, profiles, active-profile state, archive cache, database, and logs. Rename product-specific log filenames and translate product-name references in application-owned log text, retaining timestamps and diagnostic meaning. Update old absolute paths in application-owned textual configuration; preserve user payloads and JSON schemas. Move each game working folder by a same-filesystem directory rename, keeping it beside `Mods`.
5. Verify the manifest at each destination and check profile/config references before allowing ADA to start. Update any application-owned stored absolute paths that include the former directory names. Restarting the cutover must recognize completed moves and refuse ambiguous partial states.
6. On failure, keep the application stopped and restore the recorded pre-cutover locations before running the old binary again. On success, verify there are no old active roots and prevent the superseded executable from reopening them. Recovery copies remain identifiable outside the active project/data locations until separately cleaned up.

Appendix D gives complete proposed classes for the new runtime paths and API identity. The touched folder constant becomes private `APP_FOLDER_NAME`, because inspection found no external consumer. The API constant becomes `USER_AGENT`, with its consumer in `Services.Configure` updated. These follow the supplied constant naming rule; unrelated members retain their existing names and access levels.

## Workstream D: hosted repository, local metadata, and generated artifacts

- Rename `StillAccursed/Overlord` to `StillAccursed/ADA` through the repository host with appropriate access. Verify the target is available, preserve repository contents/issues/settings, and verify the new repository and Actions URLs before updating consumers. This is part of implementation scope, not an action taken while writing this proposal.
- Update `.git/config` remote URLs, README badge and link targets, API User-Agent URL, local documentation URLs, and any maintained local tooling that references the old repository or checkout path. Keep the remote name `origin`.
- Rename both the checkout and enclosing workspace directories using the absolute mapping in Workstream A. Update the saved Codex/IDE workspace location and reimport the solution. Scan ignored `.idea` files for names, paths, and folder entries: update maintained settings or regenerate IDE state with the new solution, preserving unrelated preferences.
- Enumerate old `bin`, `obj`, test results, and other generated outputs before cleanup. Rebuild in the renamed path so executable filenames, assembly metadata, dependency files, generated code, PDB source paths, and test reports use ADA. Remove superseded generated outputs from the active tree using the reviewed cleanup manifest; do not leave stale assemblies under renamed folders.
- Include `.idea/.idea.Overlord/`, its `.gitignore` module-name entry, `avalonia.xml` project/view mappings, and `workspace.xml` current paths in the IDE rename/regeneration manifest. The other four IDE files currently have no old-name content match; retain their settings. Preserve all 12 files in the sibling `output/imagegen` directory.
- Scan the current tree, active local metadata, and application-owned runtime roots for both content and filename matches. Do not patch Git object storage, third-party binaries, or compressed payload bytes with a blind text replacement. Existing Git history remains intact; current configuration and active output must use ADA.

## Implementation sequence after approval

1. Recheck current source and runtime inventories. Preserve the 13 existing modified files and all four untracked files, including the new branch model/test and notices file. Include any subsequent additions before editing.
2. Verify whether the user has already renamed the hosted repository to `ADA`. If pending, rename it on GitHub first; otherwise verify the destination. Then update the existing local `origin` to `https://github.com/StillAccursed/ADA.git`. Do not create a replacement repository or repeat an already-completed rename.
3. Prepare the complete local code/project/XAML/branding/docs/test/runtime-root patch from current files. Keep the Markdown dependency, notices copy settings, current UI behavior, Wine path fix, and all untracked additions. Validate in an isolated copy without stale build output.
4. Execute the offline runtime-data cutover before ADA's first launch, including logs and persisted absolute-path references. Confirm every game working folder retains its ownership records and recovery data.
5. Move the local checkout and enclosing workspace containers last, preserving the sibling artwork output directory. Update saved workspace/IDE paths and regenerate output from the final ADA paths. Audit the renamed notices file in both build and publish material without claiming a publish check from a build alone.
6. Replace this proposal's old-name mappings/inventory with the ADA-only completion record and run the final zero-reference audit over tracked, untracked, maintained ignored, generated, and current metadata content. Report inaccessible host/platform/runtime/workspace settings as unfinished work; do not silently narrow the scope.

The outermost container move writes to the parent `Satisfactory` directory, beyond this session's writable workspace, and hosted/runtime locations have their own access requirements. Obtain the tool/filesystem authorization required for those concrete implementation steps. No such mutation is requested or performed during this proposal revision.

## Validation and acceptance criteria

Run from the final renamed repository root:

```sh
dotnet sln ADA.sln list
dotnet restore ADA.sln
dotnet build ADA.sln --configuration Release --no-restore
dotnet test ADA.sln --configuration Release --no-build --logger "trx;LogFileName=results.trx"
```

Evaluate `RootNamespace` and `AssemblyName` on each renamed project with `dotnet msbuild <project-path> -nologo -getProperty:AssemblyName,RootNamespace`. Each must match its entry in the identity table.

Required acceptance criteria for the **whole rename**:

- All 114 C# namespace declarations, all 227 rooted using directives, all three fully qualified test types, all five project identities, all nine project references, both friend-assembly entries, and all five solution entries use ADA. Preserve project GUIDs, package versions, framework, and dependency direction.
- All eight affected XAML files agree with their code-behind; `avares://ADA/Themes/Tokens.axaml` loads. The ninth XAML file and icon move intact. Window title, header, messages, log names, API identification, comments, and test fixtures use ADA.
- Every project, solution, checkout, enclosing workspace, app-data, and game working directory uses its mapped ADA name. No old active root is left to be recreated by a superseded binary.
- Settings, profiles, favourites, active-profile state, archives, records, and recovery data survive cutover. All path and message tests reflect the new identity while preserving the original behavioral assertions.
- The hosted repository, Git remotes, README badge/link targets, API product URL, and tracked/ignored documentation use the new repository URL, which resolves directly. Verify hosted access and current local workspace settings separately from source compilation.
- Run a case-insensitive content search over tracked files, ignored maintained files, hidden current metadata, generated source/text, and the completion document. Search filenames/directories too. Both searches must return **zero old-name matches** in the active scope. Audit `.git/config` directly; exclude immutable Git history/object storage rather than treating it as editable project content. Check generated binaries/metadata and unpacked application-owned archive metadata where text search alone is insufficient.
- Remove/regenerate stale build and test artifacts; verify new assembly filenames and generated metadata use ADA, and the final checkout path is reflected in generated source/debug paths.
- Existing tests pass, including the new branch-compatibility tests, formatted-description tests, and revised one-click/quick-action tests, with renamed product messages/paths and equivalent application behavior. Validate the offline data cutover separately with disposable fixture roots covering populated legacy roots, already-completed moves, destination conflicts, and pending recovery; do not infer migration correctness from namespace compilation.

Also verify the output contains `THIRD-PARTY-NOTICES.md` with the ADA introduction and intact third-party notices. Inspect both output/publish copy metadata; verifying actual publish output is a separate check if publishing is performed. Preserve all current package identities, particularly `Markdown.Avalonia.Html`, rather than treating third-party assembly names as application branding.

After static/build/test/cutover checks, launch `dotnet run --project ADA/ADA.csproj`. Check startup, theme resources, Dashboard, Mods, Settings, placeholder pages, the updates window, visible ADA branding, current one-click/quick actions across list/grid/card layouts, formatted descriptions, disabled description navigation, separate gallery, Stable/Experimental indicators and accessible descriptions, existing profiles/settings, ownership records, and newly written `ada-` logs. Startup can invoke recovery, so it follows the cutover checks and is reported separately from compilation and unit tests.

Use the existing Windows/Linux/macOS CI matrix and distinguish compilation from platform runtime/data-cutover proof. No gameplay or mod-content changes are needed for the rename. Packaging or external integrations discovered to contain the old identity belong in the final reference inventory and must be updated before calling the rename complete.

## Review decision

**Review this as one complete rename covering all workstreams A–D and the local container moves.** There are no optional branding, data-folder, documentation, or hosted-link exclusions. Under the supplied AGENTS.md workflow, code, filesystem, runtime-data, and hosted implementation still wait for explicit approval. This turn revises only this Markdown proposal.


## Appendix A: complete proposed project files

Regenerated from the working files inspected on 26 October 2026, including the new Markdown dependency and notices-copy entry. These are review contents, not applied project files.

### `ADA/ADA.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <RootNamespace>ADA</RootNamespace>
        <AssemblyName>ADA</AssemblyName>
        <OutputType>WinExe</OutputType>
        <ApplicationManifest>app.manifest</ApplicationManifest>
    </PropertyGroup>

    <ItemGroup>
        <AvaloniaResource Include="Assets\**" />
        <Content Include="..\THIRD-PARTY-NOTICES.md" Link="THIRD-PARTY-NOTICES.md"
                 CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
    </ItemGroup>

    <ItemGroup>
        <PackageReference Include="Avalonia" />
        <PackageReference Include="Avalonia.Desktop" />
        <PackageReference Include="Avalonia.Themes.Fluent" />
        <PackageReference Include="Avalonia.Fonts.Inter" />
        <PackageReference Include="AvaloniaUI.DiagnosticsSupport">
            <IncludeAssets Condition="'$(Configuration)' != 'Debug'">None</IncludeAssets>
            <PrivateAssets Condition="'$(Configuration)' != 'Debug'">All</PrivateAssets>
        </PackageReference>
        <PackageReference Include="CommunityToolkit.Mvvm" />
        <PackageReference Include="Markdown.Avalonia.Html" />
        <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
        <PackageReference Include="Microsoft.Extensions.Http" />
        <PackageReference Include="Serilog" />
        <PackageReference Include="Serilog.Extensions.Logging" />
        <PackageReference Include="Serilog.Sinks.File" />
    </ItemGroup>

    <ItemGroup>
      <ProjectReference Include="..\ADA.Core\ADA.Core.csproj" />
      <ProjectReference Include="..\ADA.Platform\ADA.Platform.csproj" />
      <ProjectReference Include="..\ADA.Infrastructure\ADA.Infrastructure.csproj" />
    </ItemGroup>

    <ItemGroup>
      <InternalsVisibleTo Include="ADA.Tests" />
    </ItemGroup>
</Project>
```

### `ADA.Core/ADA.Core.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>ADA.Core</RootNamespace>
    <AssemblyName>ADA.Core</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Semver" />
  </ItemGroup>

</Project>
```

### `ADA.Platform/ADA.Platform.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>ADA.Platform</RootNamespace>
    <AssemblyName>ADA.Platform</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ADA.Core\ADA.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="CliWrap" />
  </ItemGroup>

</Project>
```

### `ADA.Infrastructure/ADA.Infrastructure.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>ADA.Infrastructure</RootNamespace>
    <AssemblyName>ADA.Infrastructure</AssemblyName>
    <!-- Dapper.AOT generates the SQL mapping at build time instead of with reflection at run time. -->
    <InterceptorsNamespaces>$(InterceptorsNamespaces);Dapper.AOT</InterceptorsNamespaces>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\ADA.Core\ADA.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Dapper" />
    <PackageReference Include="Dapper.AOT" />
    <PackageReference Include="Microsoft.Data.Sqlite" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Polly.Core" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="ADA.Tests" />
  </ItemGroup>

</Project>
```

### `ADA.Tests/ADA.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <RootNamespace>ADA.Tests</RootNamespace>
    <AssemblyName>ADA.Tests</AssemblyName>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\ADA.Core\ADA.Core.csproj" />
    <ProjectReference Include="..\ADA.Platform\ADA.Platform.csproj" />
    <ProjectReference Include="..\ADA.Infrastructure\ADA.Infrastructure.csproj" />
    <ProjectReference Include="..\ADA\ADA.csproj" />
  </ItemGroup>

</Project>
```

## Appendix B: complete current C# rename inventory

Paths are relative to the current repository root. This includes **114 C# files: 112 tracked and two untracked**. Each file moves under its corresponding ADA project folder and receives the namespace change shown. A new file is included even before it has been added to Git.

### `Overlord` — 24 files

| Existing file | Current namespace → proposed namespace | Git state |
| --- | --- | --- |
| `Overlord/App.axaml.cs` | `Overlord` → `ADA` | Tracked |
| `Overlord/Imaging/ModLogos.cs` | `Overlord.Imaging` → `ADA.Imaging` | Tracked |
| `Overlord/Program.cs` | `Overlord` → `ADA` | Tracked |
| `Overlord/Services.cs` | `Overlord` → `ADA` | Tracked |
| `Overlord/ViewModels/BranchCompatibilityViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | New, untracked |
| `Overlord/ViewModels/CatalogStatusViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/DashboardViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/GameInstallViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/MainViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/ModItemViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/ModListQuery.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/ModsViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/PlaceholderViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/PlanText.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/SettingsViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/UpdatesViewModel.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/ViewModels/ViewModelBase.cs` | `Overlord.ViewModels` → `ADA.ViewModels` | Tracked |
| `Overlord/Views/DashboardView.axaml.cs` | `Overlord.Views` → `ADA.Views` | Tracked |
| `Overlord/Views/MainWindow.axaml.cs` | `Overlord.Views` → `ADA.Views` | Tracked |
| `Overlord/Views/ModsView.axaml.cs` | `Overlord.Views` → `ADA.Views` | Tracked |
| `Overlord/Views/PlaceholderView.axaml.cs` | `Overlord.Views` → `ADA.Views` | Tracked |
| `Overlord/Views/PlanPanel.axaml.cs` | `Overlord.Views` → `ADA.Views` | Tracked |
| `Overlord/Views/SettingsView.axaml.cs` | `Overlord.Views` → `ADA.Views` | Tracked |
| `Overlord/Views/UpdatesWindow.axaml.cs` | `Overlord.Views` → `ADA.Views` | Tracked |

### `Overlord.Core` — 13 files

| Existing file | Current namespace → proposed namespace | Git state |
| --- | --- | --- |
| `Overlord.Core/Catalog/CatalogMod.cs` | `Overlord.Core.Catalog` → `ADA.Core.Catalog` | Tracked |
| `Overlord.Core/Catalog/CatalogVersion.cs` | `Overlord.Core.Catalog` → `ADA.Core.Catalog` | Tracked |
| `Overlord.Core/Catalog/IModCatalogSource.cs` | `Overlord.Core.Catalog` → `ADA.Core.Catalog` | Tracked |
| `Overlord.Core/Catalog/ModIdentifiers.cs` | `Overlord.Core.Catalog` → `ADA.Core.Catalog` | Tracked |
| `Overlord.Core/Games/GameBuild.cs` | `Overlord.Core.Games` → `ADA.Core.Games` | Tracked |
| `Overlord.Core/Games/GameInstall.cs` | `Overlord.Core.Games` → `ADA.Core.Games` | Tracked |
| `Overlord.Core/Games/GameInstallValidator.cs` | `Overlord.Core.Games` → `ADA.Core.Games` | Tracked |
| `Overlord.Core/Games/IGameDetector.cs` | `Overlord.Core.Games` → `ADA.Core.Games` | Tracked |
| `Overlord.Core/Profiles/ModProfile.cs` | `Overlord.Core.Profiles` → `ADA.Core.Profiles` | Tracked |
| `Overlord.Core/Resolution/DependencyResolver.cs` | `Overlord.Core.Resolution` → `ADA.Core.Resolution` | Tracked |
| `Overlord.Core/Resolution/GameVersionRange.cs` | `Overlord.Core.Resolution` → `ADA.Core.Resolution` | Tracked |
| `Overlord.Core/Resolution/ResolutionModels.cs` | `Overlord.Core.Resolution` → `ADA.Core.Resolution` | Tracked |
| `Overlord.Core/Settings/AppSettings.cs` | `Overlord.Core.Settings` → `ADA.Core.Settings` | Tracked |

### `Overlord.Platform` — 13 files

| Existing file | Current namespace → proposed namespace | Git state |
| --- | --- | --- |
| `Overlord.Platform/Detection/CrossOverDetector.cs` | `Overlord.Platform.Detection` → `ADA.Platform.Detection` | Tracked |
| `Overlord.Platform/Detection/GameDetection.cs` | `Overlord.Platform.Detection` → `ADA.Platform.Detection` | Tracked |
| `Overlord.Platform/Detection/GameProcesses.cs` | `Overlord.Platform.Detection` → `ADA.Platform.Detection` | Tracked |
| `Overlord.Platform/Detection/LinuxDetector.cs` | `Overlord.Platform.Detection` → `ADA.Platform.Detection` | Tracked |
| `Overlord.Platform/Detection/WindowsDetector.cs` | `Overlord.Platform.Detection` → `ADA.Platform.Detection` | Tracked |
| `Overlord.Platform/Formats/GameBuildReader.cs` | `Overlord.Platform.Formats` → `ADA.Platform.Formats` | Tracked |
| `Overlord.Platform/Formats/VdfNode.cs` | `Overlord.Platform.Formats` → `ADA.Platform.Formats` | Tracked |
| `Overlord.Platform/Formats/WineRegistryFile.cs` | `Overlord.Platform.Formats` → `ADA.Platform.Formats` | Tracked |
| `Overlord.Platform/Launchers/EpicManifestScanner.cs` | `Overlord.Platform.Launchers` → `ADA.Platform.Launchers` | Tracked |
| `Overlord.Platform/Launchers/SteamLibraryScanner.cs` | `Overlord.Platform.Launchers` → `ADA.Platform.Launchers` | Tracked |
| `Overlord.Platform/Paths/CaseInsensitivePath.cs` | `Overlord.Platform.Paths` → `ADA.Platform.Paths` | Tracked |
| `Overlord.Platform/Paths/WinePrefix.cs` | `Overlord.Platform.Paths` → `ADA.Platform.Paths` | Tracked |
| `Overlord.Platform/Storage/AppPaths.cs` | `Overlord.Platform.Storage` → `ADA.Platform.Storage` | Tracked |

### `Overlord.Infrastructure` — 27 files

| Existing file | Current namespace → proposed namespace | Git state |
| --- | --- | --- |
| `Overlord.Infrastructure/Catalog/CatalogSync.cs` | `Overlord.Infrastructure.Catalog` → `ADA.Infrastructure.Catalog` | Tracked |
| `Overlord.Infrastructure/Catalog/ModCatalogStore.cs` | `Overlord.Infrastructure.Catalog` → `ADA.Infrastructure.Catalog` | Tracked |
| `Overlord.Infrastructure/Catalog/ModDescription.cs` | `Overlord.Infrastructure.Catalog` → `ADA.Infrastructure.Catalog` | Tracked |
| `Overlord.Infrastructure/Catalog/SearchText.cs` | `Overlord.Infrastructure.Catalog` → `ADA.Infrastructure.Catalog` | Tracked |
| `Overlord.Infrastructure/Downloads/ArchiveDownloader.cs` | `Overlord.Infrastructure.Downloads` → `ADA.Infrastructure.Downloads` | Tracked |
| `Overlord.Infrastructure/Ficsit/FicsitApi.cs` | `Overlord.Infrastructure.Ficsit` → `ADA.Infrastructure.Ficsit` | Tracked |
| `Overlord.Infrastructure/Ficsit/FicsitApiClient.cs` | `Overlord.Infrastructure.Ficsit` → `ADA.Infrastructure.Ficsit` | Tracked |
| `Overlord.Infrastructure/Ficsit/FicsitDtos.cs` | `Overlord.Infrastructure.Ficsit` → `ADA.Infrastructure.Ficsit` | Tracked |
| `Overlord.Infrastructure/IO/AtomicFile.cs` | `Overlord.Infrastructure.IO` → `ADA.Infrastructure.IO` | Tracked |
| `Overlord.Infrastructure/IO/DiskSpace.cs` | `Overlord.Infrastructure.IO` → `ADA.Infrastructure.IO` | Tracked |
| `Overlord.Infrastructure/Installing/GameModFolders.cs` | `Overlord.Infrastructure.Installing` → `ADA.Infrastructure.Installing` | Tracked |
| `Overlord.Infrastructure/Installing/InstallRecords.cs` | `Overlord.Infrastructure.Installing` → `ADA.Infrastructure.Installing` | Tracked |
| `Overlord.Infrastructure/Installing/ModArchive.cs` | `Overlord.Infrastructure.Installing` → `ADA.Infrastructure.Installing` | Tracked |
| `Overlord.Infrastructure/Installing/ModInstallService.cs` | `Overlord.Infrastructure.Installing` → `ADA.Infrastructure.Installing` | Tracked |
| `Overlord.Infrastructure/Installing/ModInstaller.cs` | `Overlord.Infrastructure.Installing` → `ADA.Infrastructure.Installing` | Tracked |
| `Overlord.Infrastructure/Installing/ModResolutionService.cs` | `Overlord.Infrastructure.Installing` → `ADA.Infrastructure.Installing` | Tracked |
| `Overlord.Infrastructure/Installing/UPluginDescriptor.cs` | `Overlord.Infrastructure.Installing` → `ADA.Infrastructure.Installing` | Tracked |
| `Overlord.Infrastructure/Net/Retry.cs` | `Overlord.Infrastructure.Net` → `ADA.Infrastructure.Net` | Tracked |
| `Overlord.Infrastructure/Profiles/GameProfiles.cs` | `Overlord.Infrastructure.Profiles` → `ADA.Infrastructure.Profiles` | Tracked |
| `Overlord.Infrastructure/Profiles/OlpFile.cs` | `Overlord.Infrastructure.Profiles` → `ADA.Infrastructure.Profiles` | Tracked |
| `Overlord.Infrastructure/Profiles/ProfileExporter.cs` | `Overlord.Infrastructure.Profiles` → `ADA.Infrastructure.Profiles` | Tracked |
| `Overlord.Infrastructure/Profiles/ProfileStore.cs` | `Overlord.Infrastructure.Profiles` → `ADA.Infrastructure.Profiles` | Tracked |
| `Overlord.Infrastructure/Profiles/SmmProfileFile.cs` | `Overlord.Infrastructure.Profiles` → `ADA.Infrastructure.Profiles` | Tracked |
| `Overlord.Infrastructure/Settings/ArchiveCache.cs` | `Overlord.Infrastructure.Settings` → `ADA.Infrastructure.Settings` | Tracked |
| `Overlord.Infrastructure/Settings/FavouritesStore.cs` | `Overlord.Infrastructure.Settings` → `ADA.Infrastructure.Settings` | Tracked |
| `Overlord.Infrastructure/Settings/SettingsStore.cs` | `Overlord.Infrastructure.Settings` → `ADA.Infrastructure.Settings` | Tracked |
| `Overlord.Infrastructure/Threading/Concurrently.cs` | `Overlord.Infrastructure.Threading` → `ADA.Infrastructure.Threading` | Tracked |

### `Overlord.Tests` — 37 files

| Existing file | Current namespace → proposed namespace | Git state |
| --- | --- | --- |
| `Overlord.Tests/Catalog/CatalogSyncTests.cs` | `Overlord.Tests.Catalog` → `ADA.Tests.Catalog` | Tracked |
| `Overlord.Tests/Catalog/ModCatalogStoreTests.cs` | `Overlord.Tests.Catalog` → `ADA.Tests.Catalog` | Tracked |
| `Overlord.Tests/Catalog/ModDescriptionTests.cs` | `Overlord.Tests.Catalog` → `ADA.Tests.Catalog` | Tracked |
| `Overlord.Tests/Core/ModIdentifiersTests.cs` | `Overlord.Tests.Core` → `ADA.Tests.Core` | Tracked |
| `Overlord.Tests/Detection/CrossOverDetectorTests.cs` | `Overlord.Tests.Detection` → `ADA.Tests.Detection` | Tracked |
| `Overlord.Tests/Detection/GameProcessesTests.cs` | `Overlord.Tests.Detection` → `ADA.Tests.Detection` | Tracked |
| `Overlord.Tests/Downloads/ArchiveDownloaderTests.cs` | `Overlord.Tests.Downloads` → `ADA.Tests.Downloads` | Tracked |
| `Overlord.Tests/Ficsit/FicsitApiClientTests.cs` | `Overlord.Tests.Ficsit` → `ADA.Tests.Ficsit` | Tracked |
| `Overlord.Tests/Fixtures/CatalogFixtures.cs` | `Overlord.Tests.Fixtures` → `ADA.Tests.Fixtures` | Tracked |
| `Overlord.Tests/Fixtures/FakeHttpHandler.cs` | `Overlord.Tests.Fixtures` → `ADA.Tests.Fixtures` | Tracked |
| `Overlord.Tests/Fixtures/InstantTimeProvider.cs` | `Overlord.Tests.Fixtures` → `ADA.Tests.Fixtures` | Tracked |
| `Overlord.Tests/Fixtures/SmodBuilder.cs` | `Overlord.Tests.Fixtures` → `ADA.Tests.Fixtures` | Tracked |
| `Overlord.Tests/Fixtures/TempDir.cs` | `Overlord.Tests.Fixtures` → `ADA.Tests.Fixtures` | Tracked |
| `Overlord.Tests/Fixtures/UnixFactAttribute.cs` | `Overlord.Tests.Fixtures` → `ADA.Tests.Fixtures` | Tracked |
| `Overlord.Tests/Formats/GameBuildReaderTests.cs` | `Overlord.Tests.Formats` → `ADA.Tests.Formats` | Tracked |
| `Overlord.Tests/Formats/VdfNodeTests.cs` | `Overlord.Tests.Formats` → `ADA.Tests.Formats` | Tracked |
| `Overlord.Tests/Formats/WineRegistryFileTests.cs` | `Overlord.Tests.Formats` → `ADA.Tests.Formats` | Tracked |
| `Overlord.Tests/Installing/CrashSafetyTests.cs` | `Overlord.Tests.Installing` → `ADA.Tests.Installing` | Tracked |
| `Overlord.Tests/Installing/GameFolder.cs` | `Overlord.Tests.Installing` → `ADA.Tests.Installing` | Tracked |
| `Overlord.Tests/Installing/ModArchiveTests.cs` | `Overlord.Tests.Installing` → `ADA.Tests.Installing` | Tracked |
| `Overlord.Tests/Installing/ModInstallServiceTests.cs` | `Overlord.Tests.Installing` → `ADA.Tests.Installing` | Tracked |
| `Overlord.Tests/Installing/ModInstallerTests.cs` | `Overlord.Tests.Installing` → `ADA.Tests.Installing` | Tracked |
| `Overlord.Tests/Installing/TransactionCleanupTests.cs` | `Overlord.Tests.Installing` → `ADA.Tests.Installing` | Tracked |
| `Overlord.Tests/Paths/WinePrefixTests.cs` | `Overlord.Tests.Paths` → `ADA.Tests.Paths` | Tracked |
| `Overlord.Tests/Profiles/GameProfilesTests.cs` | `Overlord.Tests.Profiles` → `ADA.Tests.Profiles` | Tracked |
| `Overlord.Tests/Profiles/OlpFileTests.cs` | `Overlord.Tests.Profiles` → `ADA.Tests.Profiles` | Tracked |
| `Overlord.Tests/Profiles/ProfileExporterTests.cs` | `Overlord.Tests.Profiles` → `ADA.Tests.Profiles` | Tracked |
| `Overlord.Tests/Resolution/DependencyResolverTests.cs` | `Overlord.Tests.Resolution` → `ADA.Tests.Resolution` | Tracked |
| `Overlord.Tests/Resolution/GameVersionRangeTests.cs` | `Overlord.Tests.Resolution` → `ADA.Tests.Resolution` | Tracked |
| `Overlord.Tests/Settings/SettingsStoreTests.cs` | `Overlord.Tests.Settings` → `ADA.Tests.Settings` | Tracked |
| `Overlord.Tests/Storage/AppPathsTests.cs` | `Overlord.Tests.Storage` → `ADA.Tests.Storage` | Tracked |
| `Overlord.Tests/ViewModels/BranchCompatibilityViewModelTests.cs` | `Overlord.Tests.ViewModels` → `ADA.Tests.ViewModels` | New, untracked |
| `Overlord.Tests/ViewModels/FixAfterImportTests.cs` | `Overlord.Tests.ViewModels` → `ADA.Tests.ViewModels` | Tracked |
| `Overlord.Tests/ViewModels/FoundModsPromptTests.cs` | `Overlord.Tests.ViewModels` → `ADA.Tests.ViewModels` | Tracked |
| `Overlord.Tests/ViewModels/ModListQueryTests.cs` | `Overlord.Tests.ViewModels` → `ADA.Tests.ViewModels` | Tracked |
| `Overlord.Tests/ViewModels/ModsViewModelTests.cs` | `Overlord.Tests.ViewModels` → `ADA.Tests.ViewModels` | Tracked |
| `Overlord.Tests/ViewModels/ViewModelErrorTests.cs` | `Overlord.Tests.ViewModels` → `ADA.Tests.ViewModels` | Tracked |

## Appendix C: XAML and supporting-file inventory

All paths refer to the current repository tree.

| Existing file | Action |
| --- | --- |
| `Overlord/App.axaml` | Move with the app; update namespace/type/resource references as applicable |
| `Overlord/Themes/Tokens.axaml` | Move current contents intact, including the new dark/light FailureBrush entries |
| `Overlord/Views/DashboardView.axaml` | Move with the app; update namespace/type/resource references as applicable |
| `Overlord/Views/MainWindow.axaml` | Move with the app; update namespace/type/resource references as applicable |
| `Overlord/Views/ModsView.axaml` | Move with the app; update namespace/type/resource references as applicable |
| `Overlord/Views/PlaceholderView.axaml` | Move with the app; update namespace/type/resource references as applicable |
| `Overlord/Views/PlanPanel.axaml` | Move with the app; update namespace/type/resource references as applicable |
| `Overlord/Views/SettingsView.axaml` | Move with the app; update namespace/type/resource references as applicable |
| `Overlord/Views/UpdatesWindow.axaml` | Move with the app; update namespace/type/resource references as applicable |
| `Overlord.sln` | Rename to ADA.sln; change five project names/paths; preserve GUIDs/configurations |
| `Overlord/app.manifest` | Move with app; change assemblyIdentity name to ADA |
| `.github/workflows/ci.yml` | Use ADA.sln in restore/build/test commands |
| `.gitignore` | Use ADA in five app-relative ignore entries |
| `README.md` | Replace all product names, commands, project/source/runtime paths, and hosted URLs; preserve new renderer credits/notices link |
| `THIRD-PARTY-NOTICES.md` | New untracked file: replace one product-name introduction; retain third-party notices and existing build/publish copy wiring |
| `Directory.Build.props` | Retain contents |
| `Directory.Packages.props` | Retain current contents, including Markdown.Avalonia.Html 12.0.0-a3 |
| `.editorconfig` | Retain contents |
| `LICENSE` | Retain contents |
| `Overlord/Assets/avalonia-logo.ico` | Move with app; retain binary content |
| `Overlord/Docs/Development-Proposal.md` | Move with app; update technical paths/links; translate product identity throughout; preserve review outcomes/dates |
| `Overlord/Docs/Fix-Behaviour-Review.md` | Move with app; update technical paths/links; translate product identity throughout; preserve review outcomes/dates |
| `Overlord/Docs/Transaction-Cleanup.md` | Move with app; update technical paths/links; translate product identity throughout; preserve implementation/validation outcomes/dates |
| `Overlord/Docs/Specs/smod.md` | Move with app; update source/sample paths, runtime paths, and all product text; preserve file-format contracts |
| `Overlord/Docs/Specs/smmprofile.md` | Move with app; update source/sample paths; update all product text and runtime paths; preserve file-format contracts |
| `Overlord/Docs/ADA-Rename-Proposal.md` | Move with app; replace with an ADA-only completion record after implementation |

### Ignored local files

Move these with the application folder. Replace every product-name/reference occurrence in the seven local Markdown notes and retain their ignored status. The two reference samples currently have no raw product-name match; inspect unpacked metadata/text before the final audit and preserve third-party payloads without a matching reference. These local files are part of the complete reference audit even though they are not tracked.

- `Overlord/Docs/.claude/Progress.md`
- `Overlord/Docs/.claude/Settings.md`
- `Overlord/Docs/.claude/app-data.md`
- `Overlord/Docs/.claude/ficsit-api.md`
- `Overlord/Docs/.claude/install-engine.md`
- `Overlord/Docs/.claude/olp.md`
- `Overlord/Docs/.claude/sml.md`
- `Overlord/Docs/.refs/Mods.smmprofile`
- `Overlord/Docs/.refs/Weather_Extended-1.1.0.smod`

Ignored `.idea` state and existing `bin`/`obj` output are included in the active-reference audit. Update maintained IDE settings or regenerate state, rebuild from the final ADA paths, and remove superseded outputs using the reviewed manifest. Inspect reference samples without blindly rewriting third-party payloads.

### Current IDE state and artwork outside the repository

The seven IDE files are under `.idea/.idea.Overlord/.idea/`. Rename/regenerate the outer IDE directory and its product-specific module entry. Content matches were found in `.gitignore` (one), `avalonia.xml` (nine), and `workspace.xml` (30). `encodings.xml`, `indexLayout.xml`, `projectSettingsUpdater.xml`, and `vcs.xml` have no current old-name content match. Preserve unrelated settings.

The following paths are relative to the **enclosing workspace**, not the repository. Preserve their bytes and their already-ADA filenames; move them with the workspace container. Prompt text has been checked; the PNG contents have not been visually approved or selected as the application icon.

- `output/imagegen/ada-logo-conveyors-prompt.md`
- `output/imagegen/ada-logo-conveyors.png`
- `output/imagegen/ada-logo-mod-manager-prompt.md`
- `output/imagegen/ada-logo-mod-manager.png`
- `output/imagegen/ada-logo-prompt.md`
- `output/imagegen/ada-logo-v1-prompt.md`
- `output/imagegen/ada-logo-v1.png`
- `output/imagegen/ada-logo-v2-prompt.md`
- `output/imagegen/ada-logo-v2.png`
- `output/imagegen/ada-logo-v3.png`
- `output/imagegen/ada-logo.png`
- `output/imagegen/ada-rounded-prompt.md`

## Appendix D: complete proposed path and API classes

These code blocks are review material only. They apply the new paths after the offline cutover; they contain no automatic legacy-name fallback.

### `ADA.Platform/Storage/AppPaths.cs`

```csharp
namespace ADA.Platform.Storage;

/// <summary>
/// Where ADA keeps its own data. One root per OS, with fixed folders under it:
/// <code>
/// Windows  %LOCALAPPDATA%\ADA
/// Linux    ~/.ada
/// macOS    ~/Library/Application Support/ADA
///   Databases/   SQLite files (mod catalogue)
///   Config/      settings, profiles (Config/Profiles/*.olp) and each game's active profile
///   Staging/     downloaded archives (Staging/Archives)
///   Logs/        daily log files, the last week kept
/// </code>
/// </summary>
public sealed class AppPaths
{
    private const string APP_FOLDER_NAME = "ADA";

    public string Root { get; }

    public string Databases => Path.Combine(Root, "Databases");

    public string Config => Path.Combine(Root, "Config");

    public string Staging => Path.Combine(Root, "Staging");

    public string Archives => Path.Combine(Staging, "Archives");

    public string Logs => Path.Combine(Root, "Logs");

    public string CatalogDatabase => Path.Combine(Databases, "catalog.db");

    public string SettingsFile => Path.Combine(Config, "settings.json");

    public string Profiles => Path.Combine(Config, "Profiles");

    /// <summary>Mods the user starred on the Mods page.</summary>
    public string FavouritesFile => Path.Combine(Config, "favourites.json");

    /// <summary>Which profile each game uses.</summary>
    public string ProfilesState => Path.Combine(Config, "profiles.json");

    /// <param name="root">Overrides the OS default; for tests only.</param>
    public AppPaths(string root) => Root = root;

    /// <summary>Uses the current OS data root without creating any directories.</summary>
    public static AppPaths ForCurrentOS() => new(DefaultRoot());

    /// <summary>Creates the root and its folders if missing; safe to call every launch.</summary>
    public AppPaths EnsureCreated()
    {
        Directory.CreateDirectory(Databases);
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Archives);
        Directory.CreateDirectory(Logs);
        return this;
    }

    private static string DefaultRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), APP_FOLDER_NAME);
        }
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", APP_FOLDER_NAME);
        }
        if (OperatingSystem.IsLinux())
        {
            return Path.Combine(home, "." + APP_FOLDER_NAME.ToLowerInvariant());
        }

        throw new PlatformNotSupportedException("ADA supports Windows, Linux and macOS.");
    }
}
```

### `ADA.Infrastructure/Installing/GameModFolders.cs`

```csharp
namespace ADA.Infrastructure.Installing;

/// <summary>
/// The folders the install engine uses inside one game install. Locations stored in records and
/// the journal are relative to <c>FactoryGame</c> with <c>/</c> separators, e.g. <c>Mods/GameFeatures/X</c>.
/// </summary>
internal sealed class GameModFolders(string gameRoot)
{
    public string FactoryGame { get; } = Path.Combine(gameRoot, "FactoryGame");

    public string Mods => Path.Combine(FactoryGame, "Mods");

    public string GameFeatures => Path.Combine(Mods, "GameFeatures");

    /// <summary>Hidden working area on the game's own drive, so moves into <c>Mods</c> are renames.</summary>
    public string Work => Path.Combine(FactoryGame, ".ada");

    public string InstalledRecord => Path.Combine(Work, "installed.json");

    /// <summary>The mods the user asked for, as opposed to those installed only as dependencies.</summary>
    public string RequestedRecord => Path.Combine(Work, "requested.json");

    public string Journal => Path.Combine(Work, "journal.json");

    public string Lock => Path.Combine(Work, "lock");

    public string Transaction(string id) => Path.Combine(Work, "txn-" + id);

    /// <summary>New versions, extracted and verified before they go live.</summary>
    public string NewStaged(string txn, string reference) => Path.Combine(Transaction(txn), "new", reference);

    /// <summary>Versions moved out of <c>Mods</c> (updated or uninstalled), kept until the transaction ends.</summary>
    public string OldStaged(string txn, string reference) => Path.Combine(Transaction(txn), "old", reference);

    /// <summary>New versions taken back out of <c>Mods</c> during a rollback.</summary>
    public string Trash(string txn, string reference) => Path.Combine(Transaction(txn), "trash", reference);

    public static string LocationFor(string reference, bool gameFeature) =>
        gameFeature ? $"Mods/GameFeatures/{reference}" : $"Mods/{reference}";

    public string Absolute(string location) => Path.Combine(FactoryGame, location.Replace('/', Path.DirectorySeparatorChar));

    public void EnsureWorkFolder()
    {
        var info = Directory.CreateDirectory(Work);
        // The leading dot hides it on macOS and Linux; Windows needs the attribute.
        if (OperatingSystem.IsWindows() && !info.Attributes.HasFlag(FileAttributes.Hidden))
        {
            info.Attributes |= FileAttributes.Hidden;
        }
    }
}
```

### `ADA.Infrastructure/Ficsit/FicsitApi.cs`

```csharp
namespace ADA.Infrastructure.Ficsit;

/// <summary>Where ficsit.app lives and how ADA identifies itself to it.</summary>
public static class FicsitApi
{
    /// <summary>Identifies ADA and its repository in requests to the mod API.</summary>
    public const string USER_AGENT = "ADA/0.1 (+https://github.com/StillAccursed/ADA)";

    public static readonly Uri BaseAddress = new("https://api.ficsit.app/");

    /// <summary>A download link as other tools write it, e.g. <c>https://api.ficsit.app/v1/version/x/Windows/download</c>.</summary>
    public static string AbsoluteLink(string downloadPath) => new Uri(BaseAddress, downloadPath).ToString();
}
```

Update the existing call in `Services.Configure` to `http.DefaultRequestHeaders.UserAgent.ParseAdd(FicsitApi.USER_AGENT);`. Its HTTP timeout, client registration, and all other service wiring remain the same.

## Appendix E: current-change manifest and regression focus

Snapshot: 26 October 2026. The following changes predate this rescan and must be preserved. They are not ADA implementation changes.

### Modified tracked files — 13

- `Directory.Packages.props`
- `Overlord.Infrastructure/Catalog/ModDescription.cs`
- `Overlord.Platform/Paths/WinePrefix.cs`
- `Overlord.Tests/Catalog/ModDescriptionTests.cs`
- `Overlord.Tests/ViewModels/ModsViewModelTests.cs`
- `Overlord/Overlord.csproj`
- `Overlord/Themes/Tokens.axaml`
- `Overlord/ViewModels/ModItemViewModel.cs`
- `Overlord/ViewModels/ModsViewModel.cs`
- `Overlord/Views/ModsView.axaml`
- `Overlord/Views/ModsView.axaml.cs`
- `Overlord/Views/PlanPanel.axaml`
- `README.md`

### Untracked files — four

- `Overlord.Tests/ViewModels/BranchCompatibilityViewModelTests.cs`
- `Overlord/Docs/ADA-Rename-Proposal.md`
- `Overlord/ViewModels/BranchCompatibilityViewModel.cs`
- `THIRD-PARTY-NOTICES.md`

### Existing regression checks to retain and rerun after implementation

- `BranchCompatibilityViewModelTests`: per-branch state/glyph independence; version selection does not change API branch reports; unknown and damaged reports retain distinct descriptions.
- `ModDescriptionTests`: formatted Markdown and structural HTML remain; author CSS/scripts/comments/images/attributes are removed; fenced/inline code stays intact; plain-text changelogs do not leak CSS; invisible descriptions use the short-description fallback. Rename both fully qualified application/test types in the fallback test.
- `ModsViewModelTests`: immediate installs/uninstalls and queued changes, clicked-mod quick actions, dependency-aware removal, disabled/busy/game-running states, exact-version selection, preserved page state, and captured original game/independent queues during asynchronous planning.
- `WinePrefixTests` and the current `WinePrefix` implementation: retain mapped-drive and case-insensitive host-path behavior; do not replace `Path.Join` with the prior `Path.Combine` while renaming the namespace.
- Existing install/recovery/profile/settings/storage tests: preserve transactional behavior and schemas while updating expected product paths/messages. Data-cutover fixtures and live UI checks are separate from these existing source tests.

The rescan did not run these tests or establish a new test-case total. Do not reuse an older passing test count as evidence for the revised rename. No logo integration, runtime migration, hosted operation, generated-output cleanup, or source change occurred during this document refresh.
