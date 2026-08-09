---
name: sprig-teardown
description: >-
  Safely stop or destroy sprig workspaces, and detect/repair workspace drift. Use when the user wants
  to "tear down / remove / destroy a workspace", "stop a workspace's infra", "clean up old
  workspaces", "wipe the volumes", or when sprig reports "teardown failed" or a workspace has drifted
  (an orphaned or deleted worktree — run doctor/reconcile). For creating or inspecting workspaces use
  sprig-workspace.
---

# sprig-teardown

Tear down sprig workspaces without surprises. Teardown is irreversible (worktrees removed, volumes
wiped, infra stopped, optionally the branch deleted), so this skill's first job is to **confirm what
dies before running anything**.

## The integration contract

- Non-interactive calls take `--json --ni`; **check the exit code first**, then parse. On failure the
  payload is `{ "ok": false, "error": "…" }` (teardown has a richer failure payload — see below).
- Destructive flags are never passed without an explicit user decision:
  - `--yes` — skip the confirm prompt (required to remove without a TTY).
  - `--force` — **also delete the git branch** — "loses any commits made in the worktree".
  - `--volumes` (on `down`) — **wipes docker data**.
- Verbs are under `sprig ws …`. `rm` has the alias `remove`; `reconcile` has the alias `doctor`.

## Decide: stop vs destroy

Ask which the user actually wants — they're very different:

| Intent | Command | Effect |
|---|---|---|
| Pause work, keep everything | `sprig ws down <name>` | Stops infra; **keeps** containers' volumes, the worktree, the record |
| Pause and wipe data | `sprig ws down <name> --volumes` | Stops infra and **removes docker volumes** (data gone) |
| Destroy the workspace | `sprig ws rm <name> --yes` | Stops infra, wipes volumes, **removes worktrees**, deletes the record; keeps the branch |
| Destroy + drop the branch | `sprig ws rm <name> --yes --force` | As above **and deletes the `sprig--<name>` branch** and any commits on it |

## Procedure for a destroy (`rm`)

1. **Confirm first.** Before running, state plainly what will be destroyed (worktrees removed, volumes
   wiped, infra stopped) and **ask explicitly** whether to also delete the branch. Only add `--force`
   if the user says yes. Never assume.
2. Run: `sprig ws rm <name> --yes --json` (add `--force` only if confirmed).
3. **Handle a teardown failure.** `rm` may keep a **flagged record** and exit `1` when some layer
   couldn't be removed. The JSON is:
   ```json
   { "ok": false, "workspace": "<name>", "action": "remove", "teardownFailed": true, "issues": ["…"] }
   ```
   Surface every `issues[]` entry. Explain that **teardown is idempotent**: fix the blocker (e.g. a
   file lock, Docker down, a process holding the worktree), then run the same `sprig ws rm <name>`
   again to finish. Don't delete files by hand to "help" — re-running is the supported path.

## Drift / doctor

Record-vs-reality can drift — a worktree deleted outside sprig, or left orphaned by a half-finished
teardown. Detect and repair:

```bash
sprig ws reconcile --json           # check ALL workspaces
sprig ws reconcile <name> --json    # check one
sprig ws reconcile --repair --json  # detect AND repair drift
```

(`doctor` is an alias for `reconcile`.) Each report has `isHealthy` / `hasDrift`. This is the right
entry point for "clean up my machine" or "something's wrong with my workspaces". Show what drifted,
then offer `--repair`.

## Batch cleanup

For "remove the old / dead workspaces":

1. `sprig ws ls --json` — enumerate. Flag candidates: `teardownFailed`, stale, or whatever the user
   names.
2. **Show the exact set** you propose to remove and get one confirmation for the batch (and a separate
   yes/no on `--force`).
3. Remove them **one at a time** (`sprig ws rm <name> --yes …`), reporting each result. If one reports
   `teardownFailed`, note it and continue with the rest.

## Gotchas

- `down` (stop) keeps the workspace; only `rm` destroys it. Don't reach for `rm` when the user just
  wants to stop.
- `--force` deletes commits made in the worktree — always an explicit, separate confirmation.
- A left-behind record after `rm` isn't a bug — it's the flag that teardown was incomplete; retry.
- `reconcile --repair` fixes drift; it does not create or destroy workspaces.
