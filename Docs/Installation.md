# Installation

Download ADA from the [Releases page](https://github.com/StillAccursed/ADA/releases). Each release has a
package for every platform and processor:

| Platform | Download | Processor |
|---|---|---|
| Windows | `StillAccursed.ADA-win-x64-Setup.exe` | Intel or AMD (most PCs) |
| Windows | `StillAccursed.ADA-win-arm64-Setup.exe` | ARM (e.g. Snapdragon laptops) |
| macOS | `StillAccursed.ADA-osx-arm64.dmg` | Apple Silicon (M-series) |
| macOS | `StillAccursed.ADA-osx-x64.dmg` | Intel |
| Linux | `StillAccursed.ADA-linux-x64.AppImage` | Intel or AMD, including Steam Deck |
| Linux | `StillAccursed.ADA-linux-arm64.AppImage` | ARM |

Each package includes everything ADA needs, so there's no need to install .NET. The `.nupkg` and `releases.*.json`
files on the same page are for ADA's updater; ignore them.

ADA builds aren't code-signed yet, so each platform warns the first time ADA runs. The steps below get past it.

## Windows

1. Run the `Setup.exe`. It installs for your user only, so no administrator prompt.
2. If SmartScreen says "Windows protected your PC", choose **More info → Run anyway**.

ADA is installed to `%LocalAppData%\StillAccursed.ADA` and adds a Start menu shortcut. A `Portable.zip` is also
available for running ADA from any folder without installing.

## macOS

1. Open the `.dmg` and drag **ADA** onto **Applications**.
2. Open ADA from Applications, not from the disk image: updates can't be written to a disk image.
3. macOS blocks it the first time. Open **System Settings → Privacy & Security** and choose **Open Anyway** next to
   the message about ADA.

After each update, macOS asks once whether ADA may use its saved keychain entries (dedicated server passwords).
Choose **Always Allow**. Unsigned builds look like a different app to the keychain after every update; this goes
away once releases are signed.

## Linux

1. Make the AppImage executable: `chmod +x StillAccursed.ADA-linux-x64.AppImage`, or tick "Allow executing" in
   your file manager's properties.
2. Run it. Keep it somewhere permanent, such as `~/Applications`: updates replace the file in place.

Some distributions need FUSE 2 to run AppImages (`libfuse2` on Debian and Ubuntu). On Steam Deck, run it from
Desktop Mode.

## Uninstalling

Uninstalling removes ADA itself, but keeps your settings, profiles, backups and logs:

| Platform | Remove ADA | Your data, kept |
|---|---|---|
| Windows | **Settings → Apps → ADA → Uninstall** | `%LOCALAPPDATA%\ADA` |
| macOS | Move `ADA.app` from Applications to the Bin | `~/Library/Application Support/ADA` |
| Linux | Delete the AppImage | `~/.ada` |

Delete the data folder as well to remove everything. Mods ADA installed stay in the game until removed in ADA
or by verifying the game files through the launcher.
