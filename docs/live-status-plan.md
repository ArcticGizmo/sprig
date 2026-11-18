# Live status

Other tools want to know what sprig is running as it changes: Perch's overlay first, then agents, editors and anything else on the machine. Launching `sprig` every second to find out costs a .NET start each time and looks like malware to endpoint security. Sprig polls its own host for the same thing. So the host gets one change feed, sprig moves onto it first, and it's exposed as `sprig watch --json`, with `sprig wait` on top. This comes from [the proposal raised from Perch](live-status-proposal.md), with corrections from reading the code.

## Decisions

- **One feed, internal first.** The host gains a `subscribe` op. The pipe stays internal and unversioned like the rest of `HostProtocol`, so it stays free to change. The public contract is `sprig watch --json`.
- **A signal, then a diff, not an event per change.** A runner's status changes in more places than `Runner.Set`: a stop marks it Stopping, the build sets `Doing`, a port heard in the log sets `Port` and `Error`, the 2 s sample sets `Usage`, and a whole-workspace stop removes runners without changing their status. So the `Status` setter, and every add to or removal from `_runners`, pulses one "changed" signal. Each subscription wakes on it, takes `List()`, and sends the whole list when it differs from the last one it sent. A new place that changes status is covered without anyone remembering to raise an event. A slow subscriber only falls behind to the latest state, so nothing queues up in the host.
- **The internal line is the whole runner list.** It's tens of runners at most, and sending all of them keeps the host simple. Clients diff. `watch` turns the diff into per-app lines for the public stream.
- **Usage isn't a change** unless the subscription asked for it. Then each sample is one, every 2 s.
- **A subscriber never keeps the host alive, or starts one.** The host marks activity when a connection opens, not while it's held, so this is true already. `watch` and the app connect with `TrySend`-style calls, never `Send`, which would launch the host.
- **Workspace state is what people see.** Lines carry `RunState` (free, starting, paused, running, stopped, failed), not `WorkspaceRecord.State`. In the record, `Up` only means the workspace was made ready, not that anything is running. v1 doesn't ask Docker, so `RunState` gets no running projects, and a workspace with only containers up reads stopped or paused, as after quitting sprig with pause on. The snapshot says so with `"infra":"not-counted"`.
- **Every app the workspace has, not only those running.** Each workspace lists the apps in its plan, with the host's state for those that have a runner and `off` for those that don't, so a consumer can say "3 of 4 ready".
- **Workspace changes come from the files.** `watch` watches `workspaces/**/workspace.json` and `plan.json`, debounced by about 100 ms. Both are saved by writing a temp file and renaming it over the old one, so a rename onto either name counts as a save. A watcher buffer overflow sends a fresh snapshot. A change to a record, its plan or its runners works out the workspace's `RunState` again and sends a `workspace` line when anything shown changed.
- **The stream has its own types.** The CLI's existing `--json` writes engine records as they are: PascalCase fields under lowercase envelope keys. `watch` lines are their own types, camelCase throughout with states in camelCase, and carry `schema: 1`. The engine's records can then change without changing the stream. Consumers ignore unknown `type`s and fields. A higher `schema` means "read what you understand".
- **It outlives the host.** When the subscription ends, `watch` prints `{"type":"host","running":false}` and every app shows `off`. It retries the pipe every 2 s with a short connect timeout, because `NamedPipeClientStream.Connect` may wait out its whole timeout on a missing pipe rather than fail at once (check when building). When the host is back it sends a fresh snapshot. There's no `host.json`: one file shape fewer to keep. Workspace lines carry on while there's no host.
- **It ends only** when stdin closes (the consumer has gone) or it's killed. It's quiet while nothing changes, with no heartbeat.
- **Scope works like other commands:** the current store (`SPRIG_STORE` honoured), every workspace, or `[orch] [ws]` to narrow it. `--usage` adds CPU and memory per app.
- **No Docker in v1.** Asking Docker is the slow part of `ps`, and the host doesn't own containers. A later `--infra` can add containers on a slow beat.
- **`sprig wait [orch] [ws] [app...]`** blocks until each named app (or every app the workspace has going) is ready or finished as a job, or has settled some other way. It reuses `start`'s result and codes (`start-failed`, `start-timed-out`) and adds `not-running` for an app that has no runner. `--timeout` defaults to five minutes, like `start`. It exits 0 only when every app is ready.
- **Sprig uses the feed first.**
  - `HostStatus` subscribes instead of polling on its 1 s / 5 s timer, and reconnects as `watch` does.
  - `WaitUntilReady` (300 ms poll) and `StopAndWait` (150 ms poll) wait on the feed instead.
  - The Workspaces and Usage pages keep their 1 s timers. They read logs, Docker and usage, which the feed doesn't carry.
- **Three small CLI gaps close with it:**
  - `up --no-wait`, so `up` returns once the apps are asked for, like `start` and `resume`.
  - A `docker-unavailable` code wherever Docker isn't running or didn't answer, so a consumer needn't match message text.
  - `sprig open --json`, returning `{ ok, focused | launched }`.
- **Not doing:**
  - A status file the host keeps (option B). Add it from the same signal only if a statusline or prompt integration is wanted.
  - A public pipe (C).
  - Loopback HTTP (D).
  - Event hooks (E).
  - Log streaming. Consumers can tail `logFile`.

## Lines

```jsonc
// First, and after every reconnect.
{"type":"snapshot","schema":1,"host":{"running":true,"pid":4120,"version":"0.9.10"},"infra":"not-counted",
 "workspaces":[{"key":"storefront/2","orchestration":"storefront","name":"2","label":"feat-cart","branch":"feat-cart",
   "state":"running","paused":false,"error":null,"folder":"C:\\src\\storefront--2","dirs":["C:\\src\\storefront--2\\shop"],
   "madeForAgent":"5f0c…",
   "apps":[{"name":"shop/api","state":"ready","port":5123,"since":"2026-11-18T10:02:11Z","restarts":0,"logFile":"…"},
           {"name":"shop/web","state":"building","doing":"installing packages","since":"2026-11-18T10:02:40Z","restarts":0,"logFile":"…"},
           {"name":"shop/worker","state":"off"}]}]}

// One app changed; every field of the app, not only those that changed.
{"type":"app","workspace":"storefront/2","name":"shop/web","state":"ready","port":5124,"since":"2026-11-18T10:03:05Z","restarts":0,"logFile":"…"}

// A workspace changed (state, branch, pause, plan ...): the snapshot's shape without apps, which follow as app lines when the plan changed.
{"type":"workspace","key":"storefront/2","state":"stopped","paused":true, … }

{"type":"workspace-removed","key":"storefront/3"}
// Its plan no longer has the app, and the host isn't running it.
{"type":"app-removed","workspace":"storefront/2","name":"shop/old"}
{"type":"host","running":false}
```

An app line can also carry `pid`, `exit`, `error` and, with `--usage`, `usage` (`cpu`, `memory`, `processes`). App states are the runner states in camelCase, plus `off`.

## Pieces

Pieces 1–4 are built.

- **Client side:** `HostClient.SubscribeAsync`, with `SubscribeAnswersAsync` for the host's pid and version and `Follow` for callers that block. It connects with a 500 ms timeout. It falls back to polling `list` when a host from before `subscribe` answers "unknown request".
- **`sprig watch`:** `Sprig.Engine.Live` holds the line types and `WatchDiff`, and `LiveStatus` merges the feed with the store watcher. Stdin only counts as closed when it's redirected, because a parent that gives no stdin would otherwise end `watch` at once. Without `--json`, `watch` prints the same changes for a person to read.

1. **Engine, host:**
   - A `Changed` signal on `Supervisor`, pulsed from `Runner.Status`'s setter and from every add to or removal from `_runners`.
   - A `subscribe` op in `HostServer` (`usage` flag) that holds the connection and writes the runner list on each change, coalesced.
   - Tests: a stop, a heard port and a whole-workspace removal each reach a subscriber. Usage-only changes don't, unless asked.
2. **Engine, client:** `HostClient.Subscribe`, an async stream of runner lists (null while there's no host) that never launches the host and reconnects on a 2 s beat. A `LiveStatus` built on it that adds the store's records and plans (with a file watcher) and works out `RunState` per workspace. Both the app and the CLI use it.
3. **Sprig onto it:** `HostStatus` subscribes. `WaitUntilReady` and `StopAndWait` wait on it. Check the nav, tray count and graph still update while hidden and after the host idles out and comes back.
4. **`sprig watch --json`:** the line types, the schema, scope, `--usage`, and ending on stdin closing. Tests from a fake host: snapshot, app line, workspace line, removal, host gone and back.
5. **`sprig wait`:** the codes, `--timeout`, and `--json`.
6. **CLI gaps:** `up --no-wait`, `docker-unavailable`, `open --json`.
7. **Docs:** `watch` and `wait` in the agent guide and CLI docs, with the line shapes and the rule that consumers ignore what they don't know.
