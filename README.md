# Overlord

[![CI](https://github.com/StillAccursed/Overlord/actions/workflows/ci.yml/badge.svg)](https://github.com/StillAccursed/Overlord/actions/workflows/ci.yml)

A cross-platform mod manager for [Satisfactory](https://www.satisfactorygame.com/), built with .NET 10 and Avalonia.

Overlord's core promise: **a mod install either completes fully or leaves your game exactly as it was.** No half-installed mods, even if the app crashes mid-install.

> Status: early development (Phase 0). Game detection works; installing mods is not implemented yet.

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

## Project layout

| Project | Purpose |
| --- | --- |
| `Overlord` | Avalonia desktop app (views, view models, themes) |
| `Overlord.Core` | Domain model and logic, no OS or file-format specifics |
| `Overlord.Platform` | Game detection, launcher and Wine file formats, OS integration |
| `Overlord.Infrastructure` | ficsit.app API client, local SQLite catalogue and search |
| `Overlord.Tests` | xUnit tests with on-disk fixture installs |

## Running unsigned builds

Release builds are not code-signed.

- **macOS:** on first launch, open System Settings › Privacy & Security and choose *Open Anyway*.
- **Windows:** when SmartScreen appears, choose *More info* › *Run anyway*.

Each release lists SHA-256 checksums so you can verify the download.

## Clean-room note

Overlord is written from public API documentation and observed file formats. It does not copy or port code from Satisfactory Mod Manager.

## Licence

[MIT](LICENSE). Mod data comes from the [ficsit.app](https://ficsit.app) API, whose terms require that applications using it are open source.
