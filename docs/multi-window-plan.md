# More than one window: plan

Branch: `sprig-cd-and-start-queue`. Written 2026-11-17. Status: steps 1–3 written the same day, not yet built or tested (a NuGet restore was refused by the machine mid-session), not tried by hand. Agreed in outline (user, 2026-11-17: "Write a plan and then start building on this branch"), after asking for a way to open workspaces on different virtual desktops "so that I can see projects more spacially aligned".

## Why

Sprig allows one window per store. A second launch asks the first to show itself, and Windows answers that by switching to whichever virtual desktop the window is on. Someone who keeps one project per desktop can't have Sprig on each of them.

A second process isn't the answer: each would poll the host and docker on its own, show its own tray icon, and have its own "Stop all apps and quit", which breaks the rule that quitting Sprig stops every app. One process with several windows keeps one poll, one tray and one quit.

## Decisions

1. **One process, many windows.** The single-instance claim stays; what changes is what a second launch asks for.
2. **Each window has its own pages over shared services.** A window gets its own `MainWindowViewModel` (its own page, selection, graph and log), all over one `AppServices`. Closing a window disposes its pages: they stop listening to the host and docker polls, and the graph's container log followers end.
3. **The polls belong to the app, not a window.** The host poll ticks every second while any window shows and every five while none does (the tray's count of running apps still needs it). Docker is asked only while some window shows.
4. **A closed window is gone.** Closing a window closes it, whether or not others are open. Closing the last one leaves Sprig in the tray with no window, whether or not apps run ([resident-tray-plan.md](resident-tray-plan.md)). Opening Sprig from the tray then opens a new window on the current desktop, on the workspace the last closed window showed.
5. **Launching again brings Sprig to this desktop.** A second launch (the Start menu, `sprig-gui`, `sprig open`) brings forward a Sprig window on the virtual desktop being looked at, restoring it if minimised; when there isn't one, it opens a new window here instead of switching desktops. Windows says which desktop a window is on through `IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop`, a documented call. Elsewhere than Windows, the last window used comes forward, as now.
6. **`sprig open [id] [workspace]` opens a workspace.** A window on this desktop already showing it comes forward; otherwise a new window opens on it here. `sprig open` with nothing named behaves as in 5.
7. **"Open in a new window"** is in a workspace's ⋯ menu, and "New window" is in the tray menu. A window's title names the workspace it shows (`storefront 2 · feature-x — Sprig`), so Task View and Alt-Tab tell windows apart.
8. **One update check.** The first window checks for an update; later windows show what it found.
9. **Settings and UI state stay shared.** The last save wins. Pages already read the store again when their window comes to the front, so a change made in one window shows in another once it's focused.

## Steps

1. Pages detach on dispose; the app owns the windows and the polls (`AppWindows`); the tray works over all windows.
2. "Open in a new window", "New window", window titles.
3. The single-instance pipe carries `show` or `open <workspace key>`; the first instance answers by desktop. `sprig open` takes an optional workspace.
4. (Later) Remember each window's workspace, bounds and desktop at quit, and put them back at launch with `IVirtualDesktopManager.MoveWindowToDesktop`, which works for a process's own windows. Windows 11 keeps desktop IDs across restarts.

## As written (2026-11-17)

- **Pages**: `PageViewModel` is `IDisposable`; `FollowPolls` subscribes a page to `HostStatus` and `InfraUsage` and its `Dispose` lets go. Home, Repos, Orchestrations, Usage and Workspaces use it; Workspaces also ends its container log followers and any slot check. `MainWindowViewModel` disposes its pages, has `Title` and `ShownWorkspace`, `ShowWorkspace(key)`, a `NewWindow` hook and `ApplyUpdate`.
- **Windows**: `AppWindows` owns every `MainWindow`, the host poll (1s while any window shows, 5s while none does) and `InfraUsage.WindowShown`. It sets `ShutdownMode.OnExplicitShutdown`; the last window closing leaves sprig in the tray. A new window steps 32px down and right of the one in front. `MainWindow` no longer polls; it reports `IsShown` and `LastActivated`.
- **Desktops**: `VirtualDesktops.IsOnCurrent` calls `IVirtualDesktopManager` through `[GeneratedComInterface]` (the trimmed release has no built-in COM), which needs unsafe blocks. It lives in its own project, `Sprig.Interop`, the only one allowed unsafe code (user, 2026-11-17: "Move it to its own project just to be safe"), and takes a window handle so it needs no Avalonia. It answers null off Windows or on any failure, and `ShowHere` then treats every window as here.
- **Launching**: `SingleInstance` sends `show` or `open <key>`; `Tray` answers both with `AppWindows.ShowHere`, and has "New window". `sprig-gui --workspace <key>` opens on that workspace, which `sprig open [id] [workspace]` passes.
- **UI**: "Open in a new window" in a workspace's ⋯ menu; the window's title is bound to `Title`.
- **Tests**: `MultiWindowTests` (the pipe message both ways, `--workspace`, title and `ShownWorkspace`, the new-window hook, a disposed window no longer hearing the host poll, a later window showing the update).

## Not doing

- Moving a window to another desktop from inside Sprig. Windows' own Task View does it.
- A slimmer window that shows one workspace only. Every window is a full Sprig window, opened on a workspace.
