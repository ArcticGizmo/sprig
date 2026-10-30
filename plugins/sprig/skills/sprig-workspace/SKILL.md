---
name: sprig-workspace
description: >-
  Check out, inspect, switch and bring up sprig workspaces — isolated git worktrees with
  non-colliding ports and per-workspace docker infra, pooled per map. Use when the user wants to
  "spin up / check out a workspace", "start work on a branch in an isolated environment", "move this
  workspace onto a new branch", "which workspace am I in", "what URL / ports did feature-x get",
  "list my workspaces", or "bring a workspace's infra up". For releasing or destroying workspaces use
  sprig-teardown; for authoring .sprig.json or maps use sprig-configure.
---

# sprig-workspace

Drive the `sprig` CLI to check workspaces out of a map's pool, inspect them, move them between branches, and manage their docker infra. sprig does the work; your job is to pick the right options, parse the results, and report clearly.

## The integration contract (follow for every call)

- Always add `--json --ni` to calls you make: `--json` is a stability contract (parseable stdout); `--ni` forbids prompting (you can't answer a TTY prompt).
- **Check the exit code first.** On `0`, parse the JSON. On `1`, the payload is `{ "ok": false, "error": "…" }` — surface that message; don't retry blindly.
- Workspace records (`ws ls`, `ws info`, `pool checkout`, `ws create`) serialise in **PascalCase**: `Workspace`, `Repos[].WorktreePath`, `Ports`, `Label`, `Claimed`, `SetupFailed`. `sprig here --json` is **camelCase** and carries a `schema` version.
- If `sprig` isn't on PATH, say so and stop — it's installed from https://github.com/ArcticGizmo/sprig.

## 1. Discover what's available

```bash
sprig map ls --json            # maps you can check out from
sprig pool status <map> --json # a map's pool: claimed / free / degraded, of maxSlots
sprig repo ls --json           # registered repos
sprig ws ls --json             # every workspace
```

If there are **no maps**, hand off to `sprig-configure` to register repos and import a map (or use a single repo — see §2b).

## 2. Check out a workspace (the normal path)

A map has a bounded **pool** of workspaces (`maxSlots`, default 5). Checkout reuses a free one (least recently used) or builds a new one, then cuts a branch across every repo in it.

```bash
sprig pool checkout <map> --branch <branch> --label "<what it's for>" --json --ni
```

- **`--branch`** is required unless **`--detached`** (parked at a ref with no branch — for reviews and repros).
- **`--label`** is the workspace's headline in `ls`, `info` and the app. Always set one from what the user said they're working on; branch names say little.
- **`--from <ref>`** starts the branch from a ref (e.g. `upstream/main`). Default is each repo's base, upstream-preferred.
- **`--keep`** (default) keeps installed deps and volumes; **`--fresh`** reinstalls deps and wipes volumes. Only reuse applies — a new workspace is always fresh.
- **`--workspace <name>`** reuses a specific free workspace; **`--new`** forces a new one (fails if the pool is full).
- **`--tag <ns.key=value>`** (repeatable) marks the claim as yours. See §5.

A full pool fails with "pool '<map>' is full — release one first". List the claimed ones and ask the user which to release (sprig-teardown) — don't pick one yourself.

### 2b. Ad-hoc workspaces (outside a pool)

For a single repo, or a slice of a map's repos, use `ws create`:

```bash
sprig ws create <name> --repo <path-or-virtual-name> --json --ni
sprig ws create <name> --map <map> --only web,api --json --ni      # or --without worker
```

`--only`/`--without` need `--map`. Infra starts by default; `--skip-infra` holds off.

### Read the result

From the record, report:
- **Ports** (`Ports`: name → number), named `<capability>.<port>`.
- **Worktree paths** (`Repos[].WorktreePath`). If `Repos[].LinkPath` is set, the machine uses the Claude Code layout (§6).
- **Degraded** — `SetupFailed: true` means a `setup` command (e.g. `npm ci`) failed. The workspace is **kept**, not rolled back. Show which step failed (`Repos[].Setup[]` with `Success: false`) and say to finish setup in the worktree.

An **unmet need** fails the checkout (exit 1, rolled back), naming `repo.module needs '<capability>'`. That's a config gap: hand off to `sprig-configure` (add the provider to the map, or a map `defaults` entry).

## 3. Move a workspace on (no new checkout)

When a change is merged or queued, move the same workspace onto new work:

```bash
sprig ws switch <ws> --new <branch> --from origin/main --json --ni   # cut a new branch
sprig ws switch <ws> --branch <existing-branch> --json --ni          # check out an existing one
sprig ws switch <ws> --detach --from origin/main --json --ni         # park at a ref
```

- The old branch is never deleted, reset or pushed. Unpushed commits are reported, not blocking.
- It refuses when any worktree is dirty, naming the repos — report that; don't stash or discard on the user's behalf.
- `--only <repos>` limits it; `--fresh` also resets data volumes.
- Update the label afterwards if the work changed: `sprig ws label <ws> "<text>" --json`.

`sprig ws refresh <ws>` instead resets to the base branch, keeping deps. It discards local commits only with `--force` — confirm with the user first.

## 4. Inspect

```bash
sprig here --json              # which workspace/repo/module the cwd is in (exit 1 outside one)
sprig ws info <ws> --json      # record + drift + live containers + urls
sprig ws urls <ws> --json      # entry-point URLs; add --probe to check each is up
sprig ws status <ws> --json    # live containers only
```

In `info`, `containers: null` means Docker was unreachable — report "docker unavailable", not an error. A record with `TeardownFailed` goes to `sprig-teardown`.

Purpose fields can be set any time: `sprig ws label <ws> "<text>"`, `sprig ws note <ws> "<status>"` (e.g. "waiting on review"), `--clear` to remove.

## 5. Owning your workspaces (tags)

When you check a workspace out for a task of your own, tag it so you can find it again and clean up after yourself:

```bash
sprig pool checkout <map> --branch <b> --label "<title>" --tag agent.session=<id> --json --ni
sprig ws ls --tag agent.session=<id> --json
sprig ws tag <ws> review.pr=41 --json
sprig ws untag <ws> review.pr --json
```

Keys are `<namespace>.<name>`, lower case. Sprig stores and filters tags and never acts on them. Release clears them (they stay in the release report).

## 6. Infra

```bash
sprig ws up <ws> --json        # start docker infra (no-op without compose files)
sprig ws restart <ws> --json   # down then up, keeping volumes
```

If Docker isn't running the call fails — report the error; the workspace record is unaffected.

## 7. Working inside a workspace

- `sprig path <ws> [repo] [module]` prints a directory — use it to `cd` or to point tools at the worktree.
- `sprig cd` and `sprig code` open a terminal / VS Code window for the **user**. Don't run them yourself.
- **Claude Code layout.** When `sprig settings show --json` reports `"worktreeLayout": "claude"`, worktrees live at `<repo>/.claude/worktrees/<ws>` (with a junction at the old `<repo>--<ws>` path). A Claude session started in the main repo can then enter the workspace with its own worktree tooling. Under the default `sibling` layout it can't — work in the path from `sprig path` instead. Changing the layout (`sprig settings set --worktree-layout claude`) is the user's call and only affects new workspaces.

## Gotchas

- Checkout needs `--branch` or `--detached` with `--ni`; it never prompts in a script.
- Infra starts by default on checkout/create.
- A failed `setup` step leaves the workspace degraded, not missing.
- Two workspaces of the same map get independent ports — they run side by side by design.
- The branch shown is read live from git; `Branch` on the record is only the claim branch sprig last cut.
