# Resource usage: plan

Branch: `resource-usage`. Written and built 2026-11-13. Not yet tried by hand. Asked for by the user: "add resource usage indicators to each of the apps/infra … for debugging pressure on a machine with lots of worktrees".

## Decisions

1. **CPU is a share of the whole machine**, as Task Manager counts it, so apps and containers add up against the same 100%. Docker reports a share of one core, so its figure is divided by the host's processor count. On Docker Desktop the VM normally has every logical processor, so the two compare; a VM given fewer cores will read low.
2. **Memory is what's in RAM**: an app's working set summed over every process in its job, and a container's `MemUsage` from docker. Shared DLL pages are counted once per process, so an app with many processes reads a little high.
3. **An app counts everything it starts**, through its job object: `dotnet run`'s app, Vite's workers, and its build while it builds. A child that exits doesn't make CPU drop, since the job's CPU time includes processes that have ended.
4. **The host measures, on its own beat** (every 2 s), so CPU is always measured over the same window and a `list` stays cheap. The figure rides on `RunnerStatus.Usage`, so the app, the tray and the CLI all see the same one.
5. **Docker is asked separately and less often** (every 5 s, 30 s after it fails), only while the Workspaces page shows in a visible window, and only for up workspaces' compose projects. `docker stats` has no labels, so `docker ps` first maps container IDs to project and service: a repo's compose file may name its containers anything.
6. **Main's infra isn't measured.** It's the user's, under names sprig doesn't know; the overview's tooltip says "yours, not measured".

## As built

- **Engine**: `ResourceUsage` (CPU, memory, processes; `Text`, `Line`, `Sum`). `JobObject.Usage()` (job accounting for CPU time, `K32GetProcessMemoryInfo` per process for working set; locked against `Dispose`). `Supervisor.Sample()` and `HostServer`'s 2 s beat; a runner that isn't alive has no usage. `ContainerUsage` (ps + stats, parsing, units), `DockerCompose.Usage`, `WorkspaceService.InfraProjects` and `InfraUsage`.
- **App**: `InfraUsage` service beside `HostStatus`. App rows show `3.2% · 412 MB` under the port; the details have a Usage row (with the process count); the Infra tab has a usage column; a picked container in the graph shows its usage on the card. The overview has a CPU · MEMORY column (apps + infra, with a tooltip splitting them) and the summary adds "using N% CPU, X GB" for everything.
- **CLI**: `sprig ps` has CPU and Memory columns and a total.

## After the user's first look (2026-11-13)

- **The figures are drawn over each graph node** (user: "can we make the numbers appear above the nodes?"). CPU over memory, stacked, since on one line they're wider than the gap between nodes in a row. Like the names they never shrink below readable, so zoomed out, where two lines would reach a box's title, only memory shows: with many workspaces up, that's usually what runs out. To make room, `LayoutBox.Above` went from 52 to 92 and the layered layout's `RowGap` from 160 to 180, keeping a box one row below another clear of it. The sample renders a zoomed-in graph (`-graph-zoomed`).
- **Tests**: a real host reports usage for a running app (processes counted through the job) and none once stopped; docker parsing, joining, units and normalising; the overview's sum and summary. 504 pass. The sample renders with made-up figures.

## The Usage page (2026-11-13)

Asked for the same day: "some kind of resource usage panel where you can order and see your overall worktree utilisation". The user chose its own page in the nav over a tab on the overview, and CPU and memory only, no disk.

- **Nav**: Usage, between Workspaces and Orchestrations, "CPU and memory", a bar-chart icon.
- **Machine**: memory and CPU bars, each with what sprig runs drawn inside what's in use and everything else after it, and a line in words ("23.7 GB of 31.8 GB in use · 10 GB of it what sprig runs"). From `MachineUsage` in the engine: `GlobalMemoryStatusEx` and `GetSystemTimes`, CPU between samples.
- **Table**: every workspace using anything, with CPU, memory (and a bar against the heaviest), apps and infra running, and Open ›. A heading sorts by it, again the other way; memory, heaviest first, to begin with. A row opens out into its apps and containers, sorted the same way. Rows are moved, not rebuilt, each second, so an open row stays open. Workspaces that are up with nothing running are counted under the table.
- **Polling**: the page asks for nothing new: the host poll brings apps, `InfraUsage` (now also polled from this page) brings containers, and the machine is sampled each second while the page shows.
- **Tests**: order and totals, sorting both ways, an open row surviving a refresh, the machine's bars, and the machine sampler itself. The sample renders `-usage` and `-usage-open`.

## Not done

- No history or sparkline: only the latest figure.
- `sprig ps` doesn't show containers; `docker stats` does.
- On other platforms an app counts only the process sprig started, not its children.
