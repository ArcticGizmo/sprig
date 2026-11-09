# Workspace graph

Started 2026-11-09. A workspace's apps, jobs and infra drawn as a force-directed graph, each node showing its live state, with its log and start/stop a click away. Aspire's dashboard has the same view of a single AppHost; this one is per workspace, across repos.

## Decisions (user, 2026-11-09)

| Topic | Decision |
|---|---|
| Where | A **Graph** tab beside *Apps and logs* and *Infra, worktrees and files*. A node's **Log** opens the Apps tab on that log. |
| Infra | Infra nodes get **state, start/stop and logs**, as apps do. Container logs show in the same log pane. |
| Layout | **Settle, then drag.** The simulation runs until it settles and stops. Dragging a node pins it there. Pinned places are kept per orchestration, so every workspace of it looks the same. |

## Follow-up: dense workspaces (user, 2026-11-09)

On a real repo whose services all use the same infra and each other's urls, the force layout was a tangle. Three changes, all chosen by the user:

- **Layered layout, the default.** What uses something sits above it, so links run down the page: front ends at the top, the services they call below, infra and outside values along the bottom row. Each repo keeps a column; rows line up across columns. Loops are broken with as few links as possible running back up (Eades–Lin–Smyth), nodes are ordered within rows by barycentre sweeps keeping the order with fewest crossings, and a link that passes rows bends through the gaps. A **Layered | Force** switch keeps the force layout, which is where nodes can be dragged; the choice is remembered (`UiState.GraphForce`).
- **Infra as tags.** The infra and outside values a node uses show as tags under it, each with its state's dot, rather than as lines. Their lines appear only when one end is pointed at or picked. Too small to read when zoomed out, a tag is just its dot.
- **Focus.** With nothing pointed at or picked, links are faint. Pointing at or picking a node draws its links in full, with their values, and fades the rest.

A 3D layout was considered and turned down: on a flat screen its links cross again from any one angle, and reading it means turning it round.

## What's drawn

- **Nodes:** every app and job, every container a repo or the orchestration declares, every compose service a repo names, and a node for each value that comes from outside the workspace (a setting given a literal value, such as a shared dev database's url) or that nothing gives yet.
- **Links:** one per pair of nodes, from the app (or container) that uses a value to what the value reaches: an app → its database, `web` → `catalog-api`. Links within one repo count, unlike the orchestration picture's wires. A link only for start order (`WaitFor`, or an implied `DependsOn` with no value behind it, such as an app waiting on a job) is drawn dashed. Hovering or picking a link names the values it carries.
- **State**, from the host for apps and jobs, and from `docker compose ps` for infra: ready or healthy (green), starting, building, waiting or restarting (amber), failed, exited or unhealthy (red), stopped or not running (grey). A job that finished is green with a tick.

## Pieces

**Engine**
- `Planning/WorkspaceTopology.cs`: nodes and links from a `Plan`. Pure, so it's tested without docker or a host.
- `Compose/DockerCompose`: `Start(project, service)` and `Stop(project, service)`.
- `Compose/ContainerLogFollower`: one `docker compose logs --follow --timestamps` per service whose log is open, in a Job Object so it can't outlive the app. When it ends (the container stopped), it's started again with `--since` the last line it saw, once the container runs again.
- `Workspaces/WorkspaceService`: `InfraTargets(record, plan)` maps each infra node to its compose project and service, the same names `StartInfra` gives them. `InfraStates(record, plan)` asks `docker compose ps` once per project.

**App**
- `ViewModels/ForceLayout.cs`: the simulation, with repulsion between nodes, springs along links, a pull toward the centre, a weak pull among a repo's own nodes so repos cluster, and no overlaps. It's seeded from the node keys, so the same workspace lays out the same way every time. Pinned nodes stay put.
- `Controls/WorkspaceGraphCanvas.cs`: draws the nodes and links. Click picks a node: its links stand out, the rest dim, and a card shows its state and actions. Drag moves and pins a node, and the rest settle around it in a short animation. Dragging the background pans, the wheel zooms, and double-click opens the node's log. Right-click opens the node's menu.
- `WorkspacesViewModel.Graph.cs`: builds the graph from the plan. It applies the host's runner states and polls infra state every 3 s while the Graph tab is showing. Its commands start, stop, restart and open a log, for apps and infra alike.
- `Logs/WorkspaceLogs`: a log source can be a stream (a container follower) as well as a file the host writes.
- `UiState`: pinned node places per orchestration.

## Checks

- Tests: topology (links within a repo and across repos, wait-only links, outside values, unfilled settings, compose services, container-to-container), the layout (deterministic, no overlaps, pinned nodes kept), the follower's line parsing and `--since` resume, and infra target names matching `StartInfra`'s.
- Headless render: the sample workspace with infra added, on the Graph tab, with a node picked and its menu open.

## Not now

- A graph across workspaces or orchestrations.
- Telemetry on links (request rates, errors), which waits for the Aspire dashboard work.
