# Resident tray

Perch opens sprig with a click, and when sprig isn't running that click is a cold start of the whole app. Sprig already lived in the tray whenever apps ran, so it now lives there like a chat app: once started it stays until it's quit, and it can start at sign-in. Decided with the user on 2026-11-19.

## Decisions

- **Closing the last window never quits.** Sprig stays in the tray, with or without apps running. Only Quit ends it, and Quit still stops (or pauses) every app first, so nothing is orphaned.
- **Starting at sign-in is opt-in.** Settings → "Start sprig in the tray when I sign in". It's Windows' own per-user Run entry (`HKCU\…\CurrentVersion\Run`), read back rather than copied into `settings.json`, so it agrees with Task Manager's Startup apps, which records "off" under `StartupApproved\Run`. Turning it on here clears that. A dev build has its own entry, `sprig (Dev)`. Each launch points an entry that's on at the running exe, and uninstalling removes it. Not offered off Windows.
- **`--tray` starts with no window.** A `--tray` launch that finds sprig already running exits quietly rather than asking it for a window. The what's-new window waits for the first window to open.
- **The host still idles out.** A resident app doesn't keep the host alive: a subscriber never has, so an idle host still lets go of the store's files after 10 minutes. The host is the coordinator and owns the apps; the app is a client like the CLI and reconnects when one appears.
- **No host answers at once.** `NamedPipeClientStream.Connect` waits out its whole timeout on a pipe nobody serves, which cost every cold command and every reconnect attempt 500 ms. `HostClient.Running` opens the host's mutex instead, which it holds for its whole life, and `TrySend` and the subscription skip the connect when it isn't there.
- **`sprig update` asks before quitting sprig.** The app and host run from the folder the update swaps. When the app is open or apps are running, it asks first (before downloading), naming how many apps in how many workspaces and whether they'll be paused or stopped (`PauseOnQuit`). Where it can't ask (`--json`, piped), it refuses unless given `--yes`. A yes sends `quit` down the app's single-instance pipe, which runs the tray's Quit, then stops any host left (pausing first when set). If anything still runs from the install folder after that (another store's sprig), it refuses rather than half-apply. When the app was open, Velopack restarts it with `--tray`. A host with nothing running is shut down without asking.
- **A dev build's icon is inverted** (window and tray), decided by `AppProfile.IsDev`, the same switch that gives it its own store, so the icon always says which store you're looking at. Its tooltip reads `sprig (dev)`.

## Pieces

All built on branch `resident-tray`.

1. `HostClient.Running` and the early returns in `TrySend` and `ConnectAsync`.
2. `AppWindows` no longer shuts down on the last close; `FirstOpened` for the changelog.
3. `SignInStart`, the `--tray` flag in `Program`, the Settings panel.
4. `AppInstance.QuitMessage` and `AppInstance.Tell`; `SingleInstance.QuitRequested`, answered by `Tray.Quit`.
5. `CliUpdater`: `TakedownQuestion`, `TakeDown`, `Holding`, `--yes`.
6. `AppIcon`, inverted for dev builds.
