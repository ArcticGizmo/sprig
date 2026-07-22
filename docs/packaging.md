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

From the repo root, publish a self-contained build and pack it:

```sh
dotnet publish src/Sprig.App/Sprig.App.csproj -c Release -r win-x64 --self-contained true -o ./publish

vpk pack --packId Sprig --packVersion 0.1.0 --packDir ./publish \
  --mainExe sprig-gui.exe --packTitle Sprig --icon src/Sprig.App/Assets/sprig.ico -o ./feed
```

`./feed` then contains `Sprig-win-Setup.exe`, `Sprig-<version>-full.nupkg`, a portable zip, and a
`RELEASES` index. Packing a later `--packVersion` into the same `-o` directory appends to the feed
and generates a delta.

> `vpk` verifies that `VelopackApp.Build().Run()` is the first call in `Program.Main` — that hook
> handles the install/update lifecycle and must stay first.

## Cutting a release (CI)

Releases are automated by [`.github/workflows/release.yml`](../.github/workflows/release.yml),
triggered by **pushing a `v*` tag**. On a `windows-latest` runner it derives the version from the
tag, publishes, `vpk pack`s, and creates the GitHub Release with the Velopack feed attached. The
manual `dotnet publish` + `vpk pack` above is the local equivalent.

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

`Setup.exe` installs per-user to `%LocalAppData%\Sprig` (no admin needed) with Start Menu + Desktop
shortcuts. `--silent` installs without UI. Uninstall via `%LocalAppData%\Sprig\Update.exe
--uninstall` (or Add/Remove Programs).

## Update notifications

On launch the app checks a release feed and, if a newer version exists, shows a dismissible
"Update available" bar — notification only, it does **not** auto-update. The **About** page (bottom
of the left nav) adds a manual path: it shows the installed version and a **Check for updates**
button; when the feed has a newer release, a **Download & install** button applies it and restarts
the app. Both surfaces share `UpdateChecker` and honour `SPRIG_UPDATE_FEED`.

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
