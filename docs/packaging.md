# Packaging & updates

Sprig's desktop app is packaged for Windows with [Velopack](https://velopack.io). The build
produces an installer (`Setup.exe`), a portable zip, and a release feed. Updates are
**notification-only** for now: the app tells the user a newer version exists but never downloads
or applies it, and builds are **not code-signed** yet.

## Prerequisites

- .NET 10 SDK
- The `vpk` CLI, matching the `Velopack` NuGet version referenced by `Sprig.App` (currently 1.2.0):
  ```sh
  dotnet tool install -g vpk
  ```

## Build a release

From the repo root, publish both executables into one folder and pack it:

```sh
# The GUI (sprig-gui.exe) and the CLI (sprig.exe) publish into the SAME folder so they pack together.
dotnet publish src/Sprig.App/Sprig.App.csproj -c Release -r win-x64 --self-contained true -o ./publish
dotnet publish src/Sprig.Cli/Sprig.Cli.csproj -c Release -r win-x64 --self-contained true -o ./publish

vpk pack --packId Sprig --packVersion 0.1.0 --packDir ./publish \
  --mainExe sprig-gui.exe --packTitle Sprig --icon src/Sprig.App/Assets/sprig.ico -o ./feed
```

They share an identical .NET runtime and `Sprig.Core.dll`, so the second publish overwrites those with
the same bytes and adds the CLI's own files (`sprig.exe`, `sprig.dll`, `sprig.runtimeconfig.json`).
`--mainExe` stays `sprig-gui.exe` — that's the app Velopack launches and makes a shortcut for; `sprig.exe`
just rides along in the package and is put on PATH by an install hook (see [Install](#install)).

`./feed` then contains `Sprig-win-Setup.exe`, `Sprig-<version>-full.nupkg`, a portable zip, and a
`RELEASES` index. Packing a later `--packVersion` into the same `-o` directory appends to the feed
and generates a delta.

> `vpk` verifies that `VelopackApp.Build().Run()` is the first call in `Program.Main` — that hook
> handles the install/update lifecycle and must stay first.

## Cutting a release (CI)

Releases are automated by [`.github/workflows/release.yml`](../.github/workflows/release.yml),
triggered by **pushing a `v*` tag**. On a `windows-latest` runner it derives the version from the
tag, publishes, `vpk pack`s, generates a `SHA256SUMS.txt` describing every packed asset, and creates
the GitHub Release with the Velopack feed attached. The manual `dotnet publish` + `vpk pack` above is
the local equivalent (minus the manifest).

The `SHA256SUMS.txt` step runs **after** the pack, so it hashes exactly the bytes being uploaded, and
it fails the build if `Sprig-win-Setup.exe` is missing or the manifest doesn't match it — a release
without a matching manifest can't be installed by the one-liner, so it's caught before publish. The
manifest is sha256sum's own format (lower-case hex, two spaces, filename), so `sha256sum -c
SHA256SUMS.txt` validates a downloaded release directory as-is.

The flow for a release:

1. Run the **`/bump-version`** skill — it bumps `<Version>` in `src/Sprig.App/Sprig.App.csproj` and
   writes a new `CHANGELOG.md` section from the commits since the last tag. It does not commit or tag.
2. Commit those two files.
3. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`. The tag is the source of truth for the
   version — the skill derives the next version from the last tag, so keep the tag and the csproj in
   step.

## Changelog

`CHANGELOG.md` (repo root, [Keep a Changelog](https://keepachangelog.com/) format) is **embedded**
into the app (`Sprig.CHANGELOG.md`) at build time. On the first launch after an update, the app shows
a "What's new" window listing the entries newer than the version that last ran — driven by the
`LastSeenVersion` setting and toggleable from **Settings → Changelog**. It's also viewable any time
from **About → View changelog**. Parsing lives in `Sprig.Core/Changelog/ChangelogParser.cs`.

## Install

The primary install path is the PowerShell one-liner ([`install.ps1`](../install.ps1) at the repo root):

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/sprig/main/install.ps1 | iex
```

It resolves the latest GitHub release, fetches `SHA256SUMS.txt` + `Sprig-win-Setup.exe`, verifies the
installer against the manifest (deleting it rather than running it on any mismatch), then hands off to
Velopack's setup. Downloading via PowerShell rather than a browser skips the mark-of-the-web, so it
avoids the SmartScreen "Windows protected your PC" dialog. Pin a version with
`$env:SPRIG_VERSION = '0.4.0'` before the pipe, or a fork with `$env:SPRIG_REPO`.

> **`install.ps1` must stay pure ASCII** (no BOM) — Windows PowerShell 5.1 decodes it as the system
> codepage, and a stray em dash becomes a curly quote that silently terminates a string. Run
> `tools/test-install.ps1` after editing it; it asserts ASCII purity and exercises the manifest parsing.

`Setup.exe` itself installs per-user to `%LocalAppData%\Sprig` (no admin needed) with Start Menu + Desktop
shortcuts. `--silent` installs without UI. Uninstall via `%LocalAppData%\Sprig\Update.exe
--uninstall` (or Add/Remove Programs).

### The bundled CLI and PATH

The package ships two executables: `sprig-gui.exe` (the app) and `sprig.exe` (the CLI). An install-time
Velopack hook adds the install directory (`%LocalAppData%\Sprig\current`, where both exes live) to the
**user** PATH, so `sprig` is runnable from any newly opened terminal — no admin, machine PATH untouched.
The uninstall hook removes that entry again. Because `Environment.SetEnvironmentVariable(..., User)`
broadcasts `WM_SETTINGCHANGE`, freshly launched terminals see it immediately; already-open ones need a
restart. The hooks live in `Program.Main` (`OnAfterInstallFastCallback` / `OnAfterUpdateFastCallback` /
`OnBeforeUninstallFastCallback`) and delegate to `Sprig.App/Install/PathRegistration.cs`. Adding is
idempotent, so the re-assert on update is harmless if the entry is already there.

## Update notifications

On launch the app checks a release feed and, if a newer version exists, shows a dismissible
"Update available" bar — notification only, it does **not** auto-update. The **About** page (bottom
of the left nav) adds a manual path: it shows the installed version and a **Check for updates**
button; when the feed has a newer release, a **Download & install** button applies it and restarts
the app. Both surfaces share `UpdateChecker` and honour `SPRIG_UPDATE_FEED`.

The CLI has its own path: **`sprig update`** downloads and installs a newer release in place, and
**`sprig update --check`** just reports whether one exists. Unlike the app's flow it applies without
relaunching the UI — it hands off to Velopack's `Update.exe` and exits so `current` (which holds
`sprig.exe` itself) can be swapped, refusing up front if the desktop app is open and holding those
files. It uses the same feed as the app; the logic lives in `src/Sprig.Cli/CliUpdater.cs`.

- The feed location comes from the `SPRIG_UPDATE_FEED` environment variable (a directory path or a
  URL). If it's unset — or the app wasn't installed via Velopack (e.g. run from the build output) —
  the check is a no-op. See `src/Sprig.App/Updates/UpdateChecker.cs`.
- Verify the path headlessly without launching the UI:
  ```sh
  SPRIG_UPDATE_FEED=./feed "%LocalAppData%\Sprig\current\sprig-gui.exe" check-update
  # -> "Update available: v0.2.0 — you have v0.1.0"  (when the feed has a newer release)
  ```

## Not yet done (future)

- **Code signing** — `vpk pack` currently warns that files are unsigned. Add `--signParams` (or the
  platform-specific signing flags) once a certificate is available.
- **A hosted feed** — point `SPRIG_UPDATE_FEED` at a real HTTP release host (e.g. GitHub releases
  via a `GithubSource`) instead of a local directory.
