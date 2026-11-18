# Pause and resume

Sometimes you want a workspace off to save CPU and memory, without losing track of what was running. Pause stops it the way Stop all does, and remembers what was up; Resume starts exactly that again.

## Decisions

- **Pause is a real stop.** Docker's `pause` freezes containers and keeps their memory, and Windows has no clean way to freeze a `dotnet` process. The point is to save resources, so Pause stops everything (apps, then infra, data kept) and records what was up. Resume is a start, builds included, not a thaw.
- **What's remembered:** the apps that were going (building, waiting, starting, ready or restarting), leaving out jobs, which run again as dependencies, and anything failed; and every container that was running, including one no app uses.
- **Kept on the workspace record** (`WorkspaceRecord.Paused`), so it survives quitting sprig, the lists can say "paused", and the CLI has `sprig pause` and `sprig resume`.
- **What ends a pause:**
  - Resume starts what isn't already running, then forgets the list. If a start fails, the list is kept, so Resume can be tried again.
  - Stop all (`sprig stop`, `sprig down`) is a deliberate full stop, so it forgets the list. So does freeing the slot.
  - Starting something by hand while paused keeps the pause; Resume fills in the rest.
  - Pausing again while paused, after starting something by hand, adds what's running now to the list.
- **State:** a workspace with a pause and no app going reads `paused` (amber), even with its containers up, as they are after a quit.
- **Graph:** a paused node that isn't running reads "paused" with an amber ring, and the legend gains "paused" while the workspace is. Focus counts paused nodes as shown, so the graph keeps its shape.
- **Button:** between Start and ⋯: Pause while something runs, Resume while paused, hidden otherwise.
- **Pause on quit:** quitting sprig stops every app (no orphans). With the setting on (the default), quitting first records each workspace's running apps as a pause, so reopening sprig shows Resume. The infra was never stopped by quitting and still isn't. The tray's Quit reads "Pause everything and quit" when it applies.

## Pieces

1. Engine: `PausedRun`, `WorkspaceRecord.Paused`; `WorkspaceService.Pause`, `RememberRunning`, `EndPause`, `ResumeInfra`; `Down` and `Free` clear it; `RunState` says `paused`.
2. CLI: `sprig pause`, `sprig resume` (`--json`, `--no-wait`); `sprig workspaces` shows paused in amber; docs.
3. App: Pause/Resume button, graph colour and legend, focus, state colours in the header and overview.
4. Setting `PauseOnQuit` (default on), on the Settings page; the tray records the pause before stopping.
5. Tests for the engine pieces.
