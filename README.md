# Overlord

[![CI](https://github.com/StillAccursed/Overlord/actions/workflows/ci.yml/badge.svg)](https://github.com/StillAccursed/Overlord/actions/workflows/ci.yml)

A cross-platform mod manager for [Satisfactory](https://www.satisfactorygame.com/), built with .NET 10 and Avalonia.

Overlord's core promise: **a mod install either completes fully or leaves your game exactly as it was.** No half-installed mods, even if the app crashes mid-install.

> Status: early development. Game detection, the mod catalogue, verified downloads, the crash-safe install engine (install, update, uninstall, recovery, repair) and profile files work. The Mods and Profiles pages, and dependency resolution, are next.

## Supported setups

| OS | How Satisfactory is found |
| --- | --- |
| Windows | Steam and Epic Games Launcher, via the registry |
| Linux | Steam (native, Flatpak, Snap; Proton) and Epic via Heroic / Legendary |
| macOS | Steam or Epic running inside a CrossOver bottle |

Overlord only manages installs recorded by Steam or the Epic Games Launcher. There is no option to point it at a custom folder.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build Overlord.sln
dotnet test Overlord.sln
dotnet run --project Overlord
```

Package versions are set in one place, `Directory.Packages.props`; shared build settings are in `Directory.Build.props`.

## Project layout

| Project | Purpose |
| --- | --- |
| `Overlord` | Avalonia desktop app: views, view models, themes, and the composition root (`Services.cs`) |
| `Overlord.Core` | Domain model and logic, no OS or file-format specifics |
| `Overlord.Platform` | Game detection, launcher and Wine file formats, game processes, app data folders |
| `Overlord.Infrastructure` | ficsit.app API client, SQLite catalogue and search, verified downloads, install engine, settings, profiles |
| `Overlord.Tests` | xUnit tests with on-disk fixture installs |

## Where Overlord keeps its data

| OS | Folder |
| --- | --- |
| Windows | `%LOCALAPPDATA%\Overlord` |
| Linux | `~/.overlord` |
| macOS | `~/Library/Application Support/Overlord` |

Inside it: `Databases/` (mod catalogue), `Config/` (settings), `Staging/Archives/` (downloaded mods) and `Logs/` (one file per day, the last week kept). Please attach the latest log when reporting a bug.

## Running unsigned builds

Release builds are not code-signed.

- **macOS:** on first launch, open System Settings › Privacy & Security and choose *Open Anyway*.
- **Windows:** when SmartScreen appears, choose *More info* › *Run anyway*.

Each release lists SHA-256 checksums so you can verify the download.

## Clean-room note

Overlord is written from public API documentation and observed file formats. It does not copy or port code from Satisfactory Mod Manager.

## Credits

Overlord is built on these open-source projects. Thank you to their authors and contributors.

| Project | Used for | Licence |
| --- | --- | --- |
| [Avalonia](https://avaloniaui.net/) | Cross-platform UI | MIT |
| [Inter](https://rsms.me/inter/) (via Avalonia.Fonts.Inter) | UI typeface | SIL Open Font License 1.1 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | View models and commands | MIT |
| [Microsoft.Extensions.DependencyInjection, Http, Logging](https://github.com/dotnet/runtime) | Service wiring, HTTP clients | MIT |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | Local mod catalogue | MIT |
| [SQLite](https://sqlite.org/) (via SQLitePCLRaw) | Database engine and full-text search | Public domain (SQLitePCLRaw: Apache-2.0) |
| [Dapper](https://github.com/DapperLib/Dapper) and [Dapper.AOT](https://github.com/DapperLib/DapperAOT) | SQL mapping | Apache-2.0 |
| [Polly](https://github.com/App-vNext/Polly) | Retries and backoff | BSD-3-Clause |
| [CliWrap](https://github.com/Tyrrrz/CliWrap) | Running system commands | MIT |
| [Serilog](https://serilog.net/) (Serilog, Sinks.File, Extensions.Logging) | Log files | Apache-2.0 |
| [Semver](https://github.com/WalkerCodeRanger/semver) | Version parsing | MIT |
| [xUnit](https://xunit.net/) | Tests (not shipped) | Apache-2.0 |

Mod data and downloads come from [ficsit.app](https://ficsit.app), the Satisfactory modding community's repository, and are subject to its terms. Satisfactory is a trademark of Coffee Stain Studios; Overlord is a community project and is not affiliated with or endorsed by Coffee Stain Studios.

## Licence

Overlord's own source code is released under the [MIT licence](LICENSE) and will stay that way.

The third-party packages, fonts and services listed above are **not** covered by Overlord's licence. Each remains under its own licence and terms, linked above, and anyone redistributing Overlord must follow those as well. All of them allow use in an MIT-licensed project.
