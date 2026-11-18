# Live status for other tools (proposal)

**Status:** accepted with changes; see [live-status-plan.md](live-status-plan.md), which supersedes the contract below. Kept as the record of the ask. Raised from [Perch](https://github.com/arcticgizmo/perch) on 2026-11-18. It describes the need and one way to meet it, and lists the alternatives, so sprig can choose the shape that serves the most scenarios, which may not be the one recommended here.

## The need

Perch is a Windows tray app (Avalonia, .NET 10) that watches Claude Code sessions and shows their status in an always-on-screen overlay. A sprig integration in Perch would:

1. **Show status:** each workspace sprig knows about (slot, branch, up/down/paused/failed), and its apps (building, starting, ready on a port, exited, restarting).
2. **Act on it:** start, stop, pause and resume a workspace or one app.
3. **Open sprig** on a workspace's page, or open an app's `http://localhost:{port}`.
4. **Tie sessions to slots:** a Claude Code session whose worktree is a sprig slot (`sprig-worktrees` makes these, and records `WorkspaceRecord.MadeForAgent`) gets a badge on its row, such as "sprig: 3/4 ready". Only a tool that sees both sides can do this.
5. **Notify:** "api is ready", "api exited, restarting (3×)", "slot 2 failed to start".

Two constraints come from where Perch shows this:

- **Reads must be cheap.** The overlay is on screen all day. Whatever Perch does to learn sprig's state runs for as long as Perch does, so it must cost close to nothing when nothing is changing.
- **State must be current while things change.** People look at the overlay *while* a workspace is starting. If status is 30 seconds behind, it's wrong exactly when they're looking.

## What sprig offers today

- **App state lives in `sprig-host`.** One host per store, behind a current-user named pipe `sprig-host-{storeId}` (`HostProtocol.PipeName`). It answers one JSON line per connection, with the ops `ping`, `list`, `start`, `stop` and `shutdown` (`HostServer.Handle`). Nothing is pushed: a caller learns about a change by asking again.
- **Workspace state lives in files:** `workspace.json` and `plan.json` per workspace under the store (`WorkspaceStore`), written by whichever process changes them (the CLI or the app), not by the host.
- **The host quits after 10 idle minutes.** No host means nothing is running.
- **Sprig itself polls.**
  - `HostStatus.PollAsync` in the app asks for `list` on a 1 s timer.
  - `WorkspacesView` and `UsageView` have 1 s timers of their own.
  - `StartCommand.WaitUntilReady` (used by `start`, `restart` and `resume`) asks for `list` every 300 ms.
- **The CLI is complete for actions.** `--json` is a stable contract, it never prompts without a terminal, and the error codes (`untrusted`, `inputs-needed`, `held`, `start-failed` and so on) say what to do next. `sprig open [orch] [ws]` already brings the app to a workspace's page.
- **`sprig` is a JIT .NET exe** (Spectre.Console.Cli), not NativeAOT, so every launch pays full .NET startup. `sprig ps --json` also asks Docker for container usage, which can take seconds.

## Why polling the CLI doesn't fit

For **actions** it fits well. They're rare, a person starts them, and the CLI already does everything an action needs (preparing, trust, infra, ordering). Perch should use it as it is.

For **status** it doesn't:

- **Cost:** seeing building → starting → ready as it happens needs polling about once a second. That's about 3,600 .NET process launches an hour, each with JIT startup, and through `ps`, Docker calls too. Most of those calls report that nothing changed.
- **Endpoint security:** on machines running EDR (CrowdStrike Falcon and Defender in the case that prompted this), a GUI app launching a console exe every second is exactly the behaviour these tools score against. Perch has already had its own `gh` launches flagged.
- **Slowing down doesn't help:** polling every 30 s fixes the cost but makes the status wrong while a workspace starts, which is when it matters.

Reading the pipe directly avoids launching processes (a `list` costs well under a millisecond), but it ties every consumer to an internal, unversioned protocol and to the store-ID hash. Sprig would lose the freedom to change either.

## Who else would use live status

This isn't only a Perch need. Several consumers inside sprig poll today, and a few outside it can't easily get status at all:

| # | Consumer | Today | What it wants |
|---|---|---|---|
| S1 | **Perch** overlay, badges and notifications | nothing | changes within about 1 s, cheap when idle |
| S2 | **Sprig.App** (nav, Workspaces, Usage) | 1 s timers polling `list` | changes as they happen |
| S3 | **CLI** `start` / `restart` / `resume` waiting | 300 ms poll of `list` | "tell me when these settle" |
| S4 | **Agents** that start with `--no-wait` and later need the app ready | loop `sprig info --json` | a blocking `sprig wait` |
| S5 | **Claude Code statusline / shell prompt** ("sprig: 2 ready") | not practical (a .NET launch per render) | a read in a few ms, no process |
| S6 | **Hooks** (`sprig-context` at SessionStart) | `sprig info` (one launch, fine) | current state, once |
| S7 | **Editor extensions** (VS Code and others), later | nothing | changes, from any language |

## Options

### A. A `sprig watch --json` stream

A long-running CLI command that prints one JSON line per change (NDJSON). It sends a snapshot first, then changes. Underneath, the host gains a `subscribe` op: the pipe connection stays open and the host writes a line whenever a runner's status changes. `watch` adds workspace-level changes by watching the store's `workspace.json` files, so a consumer reads one stream.

- **Cost to sprig:**
  - an event on `Supervisor` when a runner's `Status` changes (it's set in a handful of places, mostly through `Runner.Set`);
  - a `subscribe` arm in `HostServer`, which keeps the connection open where today it answers once and closes;
  - the `watch` command itself.
- **Serves:** S1, S2, S3 and S7, and S4 (`sprig wait` is a few lines on top of the same subscription). S6 is unaffected. S5 doesn't fit: a statusline can't hold a stream open.
- **Pros:**
  - The public contract is the CLI's `--json`, which sprig already treats as stable, while the pipe stays internal.
  - Any language can read lines from stdout.
  - One long-lived process instead of thousands of short ones.
- **Cons:**
  - The consumer runs a child process and has to restart it if it dies.
  - The `watch` process costs some memory for as long as the consumer runs.

### B. A status file the host keeps

The host writes `status.json` (runners, plus host pid and start time) to the store root atomically on every change, debounced to about 250 ms. It deletes the file on clean exit. Consumers read it, or watch it with a file watcher.

- **Cost to sprig:** the same `Supervisor` event as A, plus a debounced atomic writer. Workspace state is already in files.
- **Serves:** S5 and S6 best (a plain file read, from any language, no process). S1 and S7 work through a file watcher. S2 and S3 work, but a file watcher is a weaker basis than a subscription for something sprig controls end to end. S4 needs a loop.
- **Pros:**
  - There's no process and no pipe; it's the cheapest possible read.
  - The pattern is familiar: Claude Code's own `sessions/{pid}.json` works this way, and Perch already consumes that.
- **Cons:**
  - The file goes stale if the host crashes, so readers must check that the pid is alive.
  - Writes churn during builds (the debounce helps).
  - Runner and workspace state are in different files, so every consumer merges them.
  - Usage samples (every 2 s) would mean writing every 2 s, so they're better left out or kept in their own file.
  - The file's shape becomes a public contract as much as A's lines do.

### C. Document and version the pipe protocol

Add `subscribe` (as in A) and declare the pipe a public, versioned interface. Consumers talk to it directly.

- **Cost to sprig:** the `subscribe` work, plus committing to the wire format and the pipe-name scheme.
- **Serves:** S1, S2, S3 and S7, with no process at all.
- **Cons:**
  - It freezes internals sprig might want to change.
  - Every consumer reimplements the client: pipe naming, store-ID hashing, `SPRIG_STORE`.
  - A also needs `subscribe`, and gets the same speed without the commitment, so C is A's engine without A's insulation.

### D. A loopback HTTP API with server-sent events

The host listens on `127.0.0.1:{port}`, with `GET /status` and `GET /events` (SSE).

- **Serves:** everything in A, plus browsers and web dashboards.
- **Cons:**
  - Port allocation and discovery.
  - A local network listener needs auth (a token in the store), because any local process, including a browser tab, can reach loopback.
  - On EDR-heavy machines, a new listening socket draws attention.
  - It's the most surface for the scenarios at hand.

### E. Event hooks

Settings or definitions name a command to run on events (`on-ready`, `on-exit`, `on-failed`), and sprig runs it.

- **Serves:** notifications and automation ("post to Slack when staging is ready"). It doesn't serve status display; nothing can ask "what's the state now?"
- **Cons:** it launches processes from sprig, it's a trust question (definitions can come from repos), and it complements rather than replaces the options above.

### Coverage

| | S1 Perch | S2 App | S3 CLI wait | S4 `sprig wait` | S5 statusline | S6 hooks | S7 editors |
|---|---|---|---|---|---|---|---|
| A. `watch` stream | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ |
| B. status file | ✅ | ⚠️ | ⚠️ | ⚠️ | ✅ | ✅ | ✅ |
| C. public pipe | ✅ | ✅ | ✅ | ✅ | ❌ | ⚠️ | ⚠️ |
| D. HTTP + SSE | ✅ | ✅ | ✅ | ✅ | ⚠️ | ✅ | ✅ |
| E. event hooks | ⚠️ (notify only) | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ |

(⚠️ = workable but awkward: a poll loop, a file watcher, or a client to reimplement.)

## Recommendation

The pieces that every option except E needs go first:

1. **A change event on `Supervisor`.**
2. **A `subscribe` op on the host.** It's internal, so the pipe stays free to change. The app (S2) and `WaitUntilReady` (S3) can move off their timers onto it, so sprig benefits before any outside tool does.

Then expose it as **A, `sprig watch --json`**, the public contract, with **`sprig wait`** on top for agents (S4). If a statusline or prompt integration (S5) becomes wanted, add **B** later from the same event; it's a debounced writer and nothing more.

**Choose B instead of A if sprig would rather not have long-lived consumer processes.** It covers S1, S5, S6 and S7 with a smaller public surface. Perch can work with either one. Perch's need is "changes within about 1 s, near-zero cost when idle", and both meet it.

## Proposed contract for `sprig watch --json`

### Behaviour

- **It never starts the host and never keeps it alive.** A subscriber doesn't count as activity for the idle timer.
- **It outlives the host.** When the host exits it prints `{"type":"host","running":false}` and waits for a new one, then prints a fresh snapshot. To notice a new host, either the host writes `host.json` (pid, version, started) in the store root and `watch` watches for it, or `watch` retries the pipe on a backoff (a connect to a missing pipe fails at once, so a 2–5 s retry is close to free).
- **It exits only** when stdin closes (the consumer has gone) or it's killed. A consumer that sees it exit restarts it with backoff.
- **It sends a snapshot on every (re)connect**, so a consumer never has to diff or replay.
- **It's quiet when nothing changes.** No heartbeat lines are needed; the consumer holds the process and knows if it dies.
- **It scopes like other commands:** the current store (`SPRIG_STORE` honoured), every workspace, or `[orch] [ws]` to narrow it.
- **Usage is opt-in:** `--usage` adds the CPU and memory samples the host already takes every 2 s; without it they're left out.
- **Docker infra is left out of v1.** The host doesn't own containers, and asking Docker is the slow part of `ps`. A later `--infra` could add it on a slow beat.

### Lines

```jsonc
// First line, and after each reconnect.
{"type":"snapshot","schema":1,"host":{"running":true,"pid":4120,"version":"0.9.2"},
 "workspaces":[{"key":"storefront/2","orchestration":"storefront","name":"2","label":"feat-cart","branch":"feat-cart",
   "state":"up","paused":false,"folder":"C:\\src\\storefront--2","dirs":["C:\\src\\storefront--2\\shop"],
   "madeForAgent":"5f0c…-session-id",
   "apps":[{"name":"shop/api","state":"ready","port":5123,"since":"2026-11-18T10:02:11Z","restarts":0},
           {"name":"shop/web","state":"building","doing":"installing packages","since":"2026-11-18T10:02:40Z"}]}]}

// One app changed.
{"type":"runner","workspace":"storefront/2","name":"shop/web","state":"ready","port":5124,"since":"2026-11-18T10:03:05Z","restarts":0}

// A workspace record changed (state, branch, pause, freed ...). Same shape as in the snapshot, without apps.
{"type":"workspace","key":"storefront/2","state":"down","paused":true, … }

{"type":"workspace-removed","key":"storefront/3"}
{"type":"host","running":false}
```

Field names follow the CLI's existing `--json` (camelCase, states by name). `schema` lets a consumer cope with a sprig older or newer than it expects: unknown `type`s and fields are ignored, and a higher `schema` means "read what you understand".

### What Perch needs from the fields

- `dirs` and `madeForAgent`, to put a session in Perch together with its slot.
- `port`, to open the app's URL.
- `error`, `exit` and `restarts`, to explain a red state and drive notifications.

## Actions: small asks

The CLI already covers these, and Perch would call it as is:

| Perch action | Command |
|---|---|
| Run a checked-out workspace | `sprig up <orch> <ws> --json` |
| Start / restart an app | `sprig start <orch> <ws> [app] --json --no-wait` |
| Stop | `sprig stop <orch> <ws> [app] --json` |
| Pause / resume | `sprig pause …`, `sprig resume … --json --no-wait` |
| Open sprig on it | `sprig open <orch> <ws>` |

With `--no-wait`, the command returns quickly and the stream shows the progress. `untrusted`, `inputs-needed` and `held` become an "Open in sprig to resolve" button in Perch, rather than Perch rebuilding trust and inputs.

Small additions that would help:

1. **A stable error `code` for "Docker isn't running or didn't answer"**, so a consumer can say so plainly instead of matching message text.
2. **`--no-wait` on `up`**, if it waits on more than the infra today, so `up` behaves like `start` and `resume`.
3. **`sprig open --json`**, returning `{ ok, focused | launched }`. Optional.

## Out of scope

- Remote or multi-machine status.
- Orchestrations (greyed out until their redesign).
- Log streaming. Consumers can tail the `logFile` path that `list` already returns.
