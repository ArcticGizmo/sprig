---
name: sprig-teardown
description: >-
  Release, stop or destroy sprig workspaces safely, and detect/repair workspace drift. Use when the
  user wants to "release / give back a workspace", "I'm done with this workspace", "tear down /
  remove / destroy a workspace", "stop a workspace's infra", "clean up old or abandoned workspaces",
  "wipe the volumes", or when sprig reports "teardown failed" or a workspace has drifted (an orphaned
  or deleted worktree, a broken junction — run doctor/reconcile). For checking out or inspecting
  workspaces use sprig-workspace.
---

# sprig-teardown

Get rid of workspace state without surprises. Most of the time the user wants to **release** (cheap, reversible), not **destroy** (irreversible). This skill's first job is to work out which, and to **confirm what dies before running anything destructive**.

## The integration contract

- Calls take `--json --ni`; **check the exit code first**, then parse. On failure the payload is `{ "ok": false, "error": "…" }` (teardown has a richer failure payload — see below).
- Destructive flags are never passed without an explicit user decision:
  - `--yes` (on `ws rm`) — skip the confirm prompt (required without a TTY).
  - `--force` (on `ws rm`) — **also delete the git branch** — loses any commits made in the worktree.
  - `--volumes` (on `ws down`) — **wipes docker data**.
- `rm` has the alias `remove`; `reconcile` has the alias `doctor`.

## Decide: release, stop or destroy

| Intent | Command | Effect |
|---|---|---|
| Done with a pooled workspace | `sprig pool release --workspace <ws>` | Stops containers; **keeps** worktrees, deps, volumes, branch. Clears label/note/tags. The slot goes back to the pool for the next checkout |
| Pause, keep everything | `sprig ws down <ws>` | Stops infra; keeps the workspace claimed |
| Pause and wipe data | `sprig ws down <ws> --volumes` | Stops infra and **removes docker volumes** |
| Destroy the workspace | `sprig ws rm <ws> --yes` | Stops infra, wipes volumes, **removes worktrees**, deletes the record; keeps the branch |
| Destroy + drop the branch | `sprig ws rm <ws> --yes --force` | As above **and deletes the claim branch** and any commits only on it |

For a pooled workspace (its record has a `Map`), **release is the default answer** to "I'm done". Destroy only frees disk; the pool rebuilds the slot on the next checkout anyway.

## Releasing

1. `sprig pool release --workspace <ws> --json --ni`.
2. Read `pending[]` — repos with `dirty` (uncommitted changes) or `unpushed` commits. Sprig reports them and never acts on them. Tell the user plainly:
   - On a branch: the work stays on that branch, untouched.
   - A **detached** claim: commits are on no branch, and the next checkout of this slot resets the worktree. Suggest creating a branch first.
3. `releasedLabel` / `releasedTags` show what the claim was, for the record.

## Destroying (`rm`)

1. **Confirm first.** State plainly what will be destroyed (worktrees removed, volumes wiped, infra stopped) and **ask explicitly** whether to also delete the branch. Only add `--force` if the user says yes. Never assume.
2. Run: `sprig ws rm <ws> --yes --json` (add `--force` only if confirmed).
3. **Handle a teardown failure.** `rm` may keep a **flagged record** and exit `1` when some layer couldn't be removed:
   ```json
   { "ok": false, "workspace": "<ws>", "action": "remove", "teardownFailed": true, "issues": ["…"] }
   ```
   Surface every `issues[]` entry. Teardown is **idempotent**: fix the blocker (a file lock, Docker down, a process holding the worktree), then run the same `sprig ws rm <ws>` again. Don't delete files by hand to "help".

## Drift / doctor

Record-vs-reality can drift — a worktree deleted outside sprig, one orphaned by a half-finished teardown, or (under the Claude Code layout) a missing or mis-aimed junction.

```bash
sprig ws reconcile --json           # check ALL workspaces
sprig ws reconcile <ws> --json      # check one
sprig ws reconcile --repair --json  # detect AND repair drift
```

Each report has `IsHealthy` / `HasDrift`. Show what drifted, then offer `--repair`. Repair recreates or re-points a junction, but never touches a real folder sitting where a junction should be — report that one for the user to resolve.

## Cleaning up your own workspaces

If you checked workspaces out with a tag (see sprig-workspace), find and release them by it:

```bash
sprig ws ls --tag agent.session=<id> --json
sprig pool release --workspace <ws> --json --ni   # one at a time
```

Sprig never decides a claim is abandoned. Only release workspaces you know are yours and finished, or that the user names.

## Batch cleanup

For "clean up the old / dead workspaces":

1. `sprig ws ls --json` — enumerate. Candidates: `TeardownFailed`, claimed with an old `ClaimedAt`, or whatever the user names. Show the `Label` so the user recognises them.
2. **Show the exact set** and whether you propose release or destroy for each; get one confirmation for the batch (and a separate yes/no on `--force` for any destroy).
3. Act on them **one at a time**, reporting each result. If one reports `teardownFailed`, note it and continue.

## Gotchas

- Release and `down` keep the workspace; only `rm` destroys it.
- `--force` deletes commits made in the worktree — always an explicit, separate confirmation.
- A left-behind record after `rm` isn't a bug — it flags an incomplete teardown; retry.
- `reconcile --repair` fixes drift; it does not create or destroy workspaces.
