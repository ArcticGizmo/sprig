# Start and stop mean everything: plan

Branch: `resource-usage` (follows the usage work). Written 2026-11-13.

## Why

The user: "make it so that infra does not automatically start and that we can group it together more logically with how we start and stop apps. When creating the worktree we should create the definition for the infra but not start it. Then we can start it like we do anything else with a dependency link. I am hoping this means that 'start' and 'stop' actually carry that meaning, not 'start apps' vs 'stop infra'."

Until now a workspace had two lifecycles: Bring up / Take down for its infra (and files), and Start / Stop apps for its apps. Starting an app already brought the infra it used, but stopping never touched infra, and bringing a workspace up started all of it whether anything used it or not.

## Decisions (user, 2026-11-13)

1. **Bring up and Take down go.** A workspace is ready once it's made: worktrees, ports, files and its infra's compose definitions, nothing started. Start starts what's picked and everything it depends on, infra included, preparing the workspace first when it isn't ready (never made, stopped, or failed). Stop stops. `sprig up` and `sprig down` stay on the command line as start-everything and stop-everything.
2. **Stopping one piece of infra stops just it.** Apps using it notice it's gone, as before; that's how to see how they cope.
3. **Stopping infra keeps its containers** (`docker compose stop`). Only Free, Remove and Empty data take containers away.
4. **A workspace's state is read from what's running**: running, stopped, failed, free. The stored state still says whether it's prepared (Up) or released (Down), which ports and checkout take-over need, but isn't shown as "up" or "down".

## Design

- **Engine**: `Up` prepares; it starts infra only when given what's starting (`Starting`), as Start does for a workspace that isn't ready. With nothing starting it writes the compose files and starts nothing, and main skips the check that its own infra is listening (Start does that). `Down` stops apps and infra with `compose stop`, releases random ports and marks it released. `CheckOut` prepares.
- **App**: the header has Start ▾, Restart, Stop all, Free and the rest; no Bring up, Take down or Stop apps. Making a workspace prepares it. A container's Start prepares the workspace if need be, then `compose up` for that service (which makes its container the first time). The overview's chips and states are Running, Failed, Stopped; its bulk buttons are Start and Stop; Start brings everything (infra and apps).
- **CLI**: `sprig start` and `sprig stop` take apps or infra by name; with none, everything. `sprig stop` with no names is `sprig down`. `sprig up --no-start` prepares only (was `--no-apps`, which started the infra).

## As built (2026-11-13)

- **Engine**: `Up` with no `Starting` writes the compose files and starts none (`StartInfra` with an empty set: "ready; it starts with what uses it"); main skips its own-infra check until something starts. `Starting.All` for everything. `Down` stops apps, then `compose stop` (`DockerCompose.StopAll`), keeping containers. `RunningProjects` (one `docker ps`) and `WorkspaceService.RunState` give the shown state; `DefinitionsChanged` compares definition files' times with `LastUpAt`.
- **App**: header Start ▾, Restart, Stop all, Free, Inputs, ⋯ (Make ready again, Empty data, Remove). Start all makes the workspace ready first when it isn't, or when a definition changed. The header's state, the nav count, the overview's state, chips (Running, Failed, Stopped) and LAST USED column, and the Repos and Orchestrations pages' lists all come from `RunState`. Bulk Start starts everything; bulk Stop stops everything. A container's Start in the graph makes the workspace ready if need be, then `compose up` for that service. Empty data starts again what was running and the infra it uses.
- **CLI**: `AppsSettings.Split` sorts names into apps and infra. `sprig start` prepares a workspace that isn't ready (`--trust` for its definitions), starts named infra, and the infra named apps use. `sprig stop` with names stops just those; without, everything. `sprig ls` and `sprig info` show running/stopped; `info`'s infra table has a state column. `sprig up --run`'s Ctrl+C stops the infra too.
- **Tests**: a workspace made ready defines its infra and starts none, starting everything starts it, and stopping uses `compose stop`; main is made ready without its infra and won't start without it; names split into apps and infra. 511 pass.

## No window for start and stop (2026-11-13)

The user, after the first try: Start opened the progress window (Plan, Worktree, Copy into worktrees, Ports, Files, Start), which said nothing the graph doesn't: "Is there a world in which we just remove the dialog?" It opened because Stop all releases the workspace (so random ports go back and the checkout is free), and the next Start makes it ready again through the same window Bring up used.

- Start, Make ready again and Stop all run without a window (`RunQuietlyAsync`): a line under the header says "Starting web--1…", and "starting its infra…" while compose runs, with Cancel beside it. Approvals still open the review, inputs still say where to give them, and a failure's whole message stays on the record, which the header's error box shows.
- The infra about to start reads "starting" on the graph at once, and docker's polls leave it so until it's running.
- Making a workspace, Free, Empty data, Remove and the overview's bulk actions keep the window: they're slower, or they delete things.

## Ports hold still (2026-11-13)

The user: "does it make sense that ports are handed back on stop? Should it not just be handed back on free? Otherwise each time you start/restart things they will shift under you." Stable ports (the default) never were: they're kept through stop and free, and go only on remove. Random ports were handed back by `Down`, which made sense when coming up was rare, but with stop and start the everyday loop they'd move every time. Now `Down` keeps every lease; `Free` hands random ones back, and `LeasePorts` draws fresh random ports only for a slot that was free (a new branch) or a new workspace.

## Not done

- Stopping one container doesn't stop the apps that use it (decision 2), and nothing warns that they're using it.
- `DefinitionsChanged` looks at file times, so saving a definition unchanged counts as a change, and a definition read from somewhere else (an include, say) isn't noticed.
- The Repos and Orchestrations pages read containers' states from the last docker measure, which only runs while the Workspaces or Usage page shows.
