---
name: sprig-workspace
description: >-
  Create, inspect and bring up sprig workspaces — isolated git worktrees with non-colliding ports
  and per-workspace docker infra. Use when the user wants to "spin up / create a workspace", "make an
  isolated environment from a stack", "bring a workspace's infra up", "list / show running
  workspaces", or "what ports did feature-x get". For destroying or stopping workspaces use
  sprig-teardown; for authoring .sprig.json or stacks use sprig-configure.
---

# sprig-workspace

Drive the `sprig` CLI to create workspaces from a stack (or a single repo), inspect them, and manage
their docker infra. sprig does the work; your job is to pick the right options, parse the results, and
report clearly.

## The integration contract (follow for every call)

- Always add `--json --ni` to non-interactive calls: `--json` is a stability contract (parseable
  stdout); `--ni` forbids prompting (an agent can't answer a TTY prompt).
- **Check the exit code first.** On `0`, parse the JSON. On `1`, the payload is `{ "ok": false,
  "error": "…" }` — surface that message; don't retry blindly.
- Workspace verbs live under `sprig ws …` (alias `sprig workspace …`). There is no bare
  `sprig create`.
- If `sprig` isn't on PATH, say so and stop — it's installed from https://github.com/ArcticGizmo/sprig.

## 1. Discover what's available

```bash
sprig stack ls --json      # stacks you can create from
sprig repo ls --json       # registered repos
sprig ws ls --json         # existing workspaces (workspace, repos, ports, status)
```

If there are **no stacks**, a workspace still can't be created from one — hand off to the
`sprig-configure` skill to register repos and build a stack (or create from a single repo with
`--repo <path>`).

## 2. Create a workspace

```bash
sprig ws create <name> --stack <stack> --json --ni
```

- **Partial workspaces** — when the user only needs some of the stack's repos:
  - `--only web,api` — just these repos, or
  - `--without worker` — every repo except these.
  - These narrow a *stack*, so they require `--stack`. Naming a repo not in the stack is a hard error.
- **`--skip-infra`** — create only, don't start docker. By default infra **starts automatically**
  after create (a stack with no compose files is a silent no-op).
- **Single repo, no stack**: `sprig ws create <name> --repo <path> --json --ni`.

### Read the result

The JSON is the workspace record. Report, from it:
- **Allocated ports** (`ports`: name → number) — the non-colliding ports this workspace got.
- **Worktree paths** (`repos[].worktreePath`) — where each repo's isolated checkout landed
  (`<repo>--<name>` on a fresh `sprig--<name>` branch).
- **Partial?** (`isPartial`, `excludedRepos`, `skippedPorts`) — call these out if set.
- **Soft setup failure** — if any `repos[].setup[].success == false`, the workspace was **kept**, not
  rolled back; a `setup` command (e.g. `npm ci`) failed. Tell the user to finish setup by hand in the
  worktree, and show which command failed. (A bad worktree/env/compose *does* roll back; setup does
  not.)

An **unbound input** is a hard failure at create time (exit 1) — the error names the repo, the input,
and its example. That's a config gap: hand off to `sprig-configure` to add the binding to the stack.

## 3. Bring infra up / restart

```bash
sprig ws up <name>         # start the workspace's docker infra
sprig ws reset <name>      # down then up (restart)
```

`up` is a no-op for a stack with no compose files. If Docker isn't running the call fails — report the
error; the workspace record itself is unaffected.

## 4. Inspect

```bash
sprig ws info <name> --json   # one-stop: record + drift report + live containers
sprig ws status <name> --json # live containers only (subset of info)
sprig ws ls --json            # all workspaces
```

`info` folds in three things: the stored record, a **drift** report (record-vs-reality — e.g. a
worktree deleted out from under sprig), and the **live containers**. If `containers` is `null`, Docker
was unreachable (report "docker unavailable", not an error). A `teardownFailed` workspace is flagged
here too — route those to `sprig-teardown`.

## 5. Jump into a workspace (guidance, don't run it yourself)

To work inside a workspace, point the user at:
- `sprig cd <name>` — opens a **new terminal** already in the repo/module directory (interactive
  picker, or name the repo/module).
- `sprig path <name>` — just prints the directory, for scripts:
  `Set-Location (sprig path <name>)`.

Don't run `sprig cd` on the user's behalf — it spawns a terminal window; it's for them to run.

## Gotchas

- `--only` / `--without` need `--stack`; they're two ways to say the same subset.
- Infra starts by default on create — use `--skip-infra` to hold off.
- A failed `setup` step is a **warning**, not a failure: the workspace exists.
- Two workspaces of the same stack get independent port sets — they run side by side by design.
