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

## Install

`Setup.exe` installs per-user to `%LocalAppData%\Sprig` (no admin needed) with Start Menu + Desktop
shortcuts. `--silent` installs without UI. Uninstall via `%LocalAppData%\Sprig\Update.exe
--uninstall` (or Add/Remove Programs).

## Update notifications

On launch the app checks a release feed and, if a newer version exists, shows a dismissible
"Update available" bar. It does **not** auto-update.

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
- **Applying updates** — the download/apply/restart flow is intentionally not wired; only
  notification is.
