# Releasing

Releases are built by [release.yml](../.github/workflows/release.yml) with [Velopack](https://velopack.io), which
makes the installers and the update packages ADA's updater reads.

## Making a release

1. Make sure `main` builds and CI passes.
2. Write the release notes in `Docs/Releases/<version>.md` (e.g. `Docs/Releases/0.8.0.md`) and commit them.
3. Tag the commit and push the tag. The version comes from the tag, without the `v`:

   ```bash
   git tag v0.8.0
   git push origin v0.8.0
   ```

4. The workflow publishes and packs every target on its own runner, then creates a **draft** release. The notes
   file becomes the release description and is included in each update package. Without one, GitHub writes notes
   from the commits instead.
5. Check the draft's assets and notes, then publish it.

`<Version>` in `Directory.Build.props` is only for local builds; releases always take the tag's version.

## What each target produces

| Target | Runner | Packages |
|---|---|---|
| `win-x64`, `win-arm64` | `windows-latest` | `Setup.exe`, `Portable.zip` |
| `linux-x64` | `ubuntu-latest` | `.AppImage` |
| `linux-arm64` | `ubuntu-24.04-arm` | `.AppImage` |
| `osx-arm64`, `osx-x64` | `macos-latest` | `.dmg` holding `ADA.app` |

Every target also produces a `.nupkg` and `releases.<target>.json`. Each target is its own Velopack channel, so an
installed copy only ever updates to the package for its own platform and processor.

Notes on the setup:

- **Pack ID `StillAccursed.ADA`, not `ADA`.** On Windows Velopack installs to `%LocalAppData%\<packId>` and deletes
  that folder on uninstall. `%LocalAppData%\ADA` holds the user's data, so the two must differ.
- **Self-contained, untrimmed, ReadyToRun.** Set in `ADA.csproj` whenever a runtime identifier is given. linq2db
  and Avalonia rely on reflection, so trimming and Native AOT aren't used.
- **macOS disk image.** Velopack's `.pkg` is skipped (`--noInst`). The workflow unpacks Velopack's portable
  `ADA.app` with `ditto` and wraps it in a `.dmg` with an Applications shortcut. The `.app` carries Velopack's
  update data, so a copy dragged to Applications updates normally.

## Signing

Builds aren't signed with a certificate yet. Users see the warnings described in
[Installation.md](Installation.md).

- **macOS** is ad-hoc signed (`--signAppIdentity -`), which `vpk` applies to the whole bundle with the hardened
  runtime. Without it, only the launcher has the .NET SDK's own signature. `codesign --verify` then rejects
  the bundle, and a downloaded copy is reported as damaged instead of offering **Open Anyway**.
- **Windows** is unsigned.

Adding signing later:

- **macOS (Developer ID).** Import the certificate into a temporary keychain in the macOS job. Replace `-` with
  `"Developer ID Application: …"` and add `--notaryProfile <profile>`. Notarisation may also need
  `--signEntitlements` granting the .NET JIT (`com.apple.security.cs.allow-jit`). Notarise the `.dmg` too. A
  stable signature also stops the keychain prompt after each update.
- **Windows.** Pass `--signParams` (signtool) or `--azureTrustedSignFile` (Azure Trusted Signing) to `vpk pack`.

Keep certificates and passwords in repository secrets.

## App icons

The icons are generated from `ADA/Assets/ada-icon.png` and committed. To regenerate them on macOS after changing
the artwork:

```bash
swift build/make-icons.swift ADA/Assets/ada-icon.png
```

This writes `ADA/Assets/ada.ico` (Windows and the window icon), `build/icons/ada.icns` (macOS) and
`build/icons/ada.png` (Linux).
