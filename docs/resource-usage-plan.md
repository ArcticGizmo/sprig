# Resource usage: plan

Branch: `resource-usage`. Written and built 2026-11-13; tried by hand and released in v0.9.2. Asked for by the user: "add resource usage indicators to each of the apps/infra … for debugging pressure on a machine with lots of worktrees".

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

## Gaps closed (2026-11-16)

Branch `start-stop-usage-gaps`, after the user had tried it by hand.

- **Sparklines on the Usage page, ten minutes** (user chose the Usage page only, not the Workspaces page or graph). `UsageHistory` (in the app, beside `InfraUsage`) keeps a ring of 300 two-second slots per app (`AppId`, recorded on each host answer, which comes every 5 s even hidden) and per container (`InfraId`, on each docker measure). A series silent for the whole span is dropped. `OfWorkspace` adds up a workspace's apps and its projects' containers, stopped ones included; each part's gaps of up to 4 slots are bridged first, or a container measured every 5 s makes the total saw up and down. The `Sparkline` control draws points already scaled 0..1, bridging the same short gaps and breaking on longer ones, with a dot on the latest figure. CPU scales from 0 to its peak (at least 1%), so an idle app lies flat; memory from its low to its peak, the span at least a tenth of the peak, so growth shows without magnifying wobble. Each sparkline's tooltip gives the peak (CPU) or range (memory). Parts are now kept objects (`UsagePart` by series ID) moved in place like rows, and only an open row's parts work out their trends. The sample fills made-up history.
- **`sprig ps` lists containers**: a second table (workspace, container, CPU, memory) for every ready workspace but main, from one `docker stats`; a line when docker doesn't answer; one total for apps and containers. `--json` is `{ apps, infra }`.
- **Linux and macOS count an app's children.** `ProcessTable.Take()` reads every process with its parent (`/proc/*/stat` on Linux, USER_HZ fixed at 100; one `ps -axo pid=,ppid=,rss=,time=` elsewhere), once per sample beat and only while something is alive; `Tree` adds up the process sprig started and its descendants. Unlike a job, a child that exits takes its CPU time with it, so a sample just after can read low (clamped at 0). The root process alone stays as the fallback when the table can't be read.

## Not done

- History is only kept while the app runs, and containers' only while a page measures them.
