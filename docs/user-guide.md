# User guide

This walks through the full sprig flow — register repos, compose a map, check out an isolated
workspace, run its infra, and tear it down — from both the **desktop app** and the **CLI**. For the
file formats, see the [configuration reference](config-reference.md).

## Concepts in 30 seconds

- **Repo** — a git repo with a committed `.sprig.json` that declares what it **provides** to others
  (ports, and values derived from them) and what it **needs** from them. Self-describing.
- **Map** — a named set of repos you compose. It stores only the *deviations* from automatic wiring
  (which provider wins when several could; a manual fallback for a need you've left out). Everything
  else is derived from the repos' own provides/needs. Lives centrally, never in a repo.
- **Workspace** — a live slice of a map: one git worktree per repo (parked in **detached HEAD** at
  the base until you claim it and name a branch), a set of allocated ports, and optional docker infra.
  The pooled flow — check out, work, release — is the recommended way to get one; see the
  [pooled workflow](pooled-workflow.md).
- **Central store** — `%LOCALAPPDATA%\sprig`: the repo registry, map definitions, and the
  per-workspace records that are the source of truth for what should exist.

The heart of it: wiring is **derived by capability name**. A repo's `need` is satisfied by whichever
selected repo `provides` that capability — so composing repos is mostly just *selecting* them.

---

## Desktop app

The app opens on **Home** and the left nav is grouped by what you're doing:
**Home**, then **Set up** (Repos, Maps), then **Run** (Workspaces) — the order you actually work in.
Each page is a two-pane layout: a list on the left, detail on the right (the list caps at a readable
width, and the detail takes the rest on a wide window).

### Home
The front door. It shows where you are along the **repo → map → workspace** pipeline as a journey
rail with live counts, plus a single **next best action** button that always points at the right next
step (add a repo → compose a map → spin up a workspace). Clicking a rail tile jumps to that page. Once
you have workspaces, Home also lists your recent ones with quick actions.

First time? **Walk me through setup** starts a guided strip across the top that launches the real
Add-repo → Compose-a-map → Spin-up flows in order and auto-advances as you go, with **Back** and
**Skip**.

### Repos
- **Add repo** — browse to a git folder. If it already has a `.sprig.json` it's registered as-is; if
  not, sprig inspects the repo, proposes one (a **provides** for each listen/service port it detects,
  with any consumed URLs flagged as needs to declare by hand), writes it, and registers — in one step.
- Selecting a repo shows its declared **provides / needs** per module. **Edit** opens an in-place
  editor for the `.sprig.json`; **Open in…** launches Explorer / VS Code / a terminal.
- **Isolate this repo** spins up a workspace straight from a single repo — no map needed (its needs,
  if any, resolve from the map's defaults or fail with a gap list).
- **Unregister selected** removes it from the registry (never touches the repo on disk).

#### Monorepos — one repo, many modules
A repo is made of **modules** — slices with their own provides/needs, `.env` files, docker compose
files and setup commands. A single-app repo has one module; a monorepo has several (e.g. `apps/web`,
`apps/api`).
- **provides/needs are per module.** A sibling module can provide exactly what another needs, and it
  wires **locally** — nearest-wins, no map involved. A monorepo is its own little map; only the needs
  nothing local satisfies bubble up.
- In the editor, each module is a **tab**. Give it a **name** and an optional **path** (the
  subdirectory it lives in) — its env/compose paths resolve under that path, and its setup runs there.
  Its **PROVIDES** and **NEEDS** sections author the capability surface: a provide's outputs are a
  **port** (auto-allocated, optionally pinned) or a **derived** string built from this capability's
  own port.
- **+ Add module** adds a tab; **Delete module** removes one — all the way down to zero, so you can
  rebuild a repo's modules from scratch.
- A single-app repo may keep its `env`/`compose`/`setup`/`provides`/`needs` at the top level; sprig
  treats that as one implicit `app` module.

### Maps
- **New map** — name it (validated live — path-safe characters only), and tick the repos to include.
  That's usually all a map needs: wiring is derived from the repos' provides/needs by capability name.
- The map preview shows each repo's **provides / needs** and how they wire — and surfaces the two
  things a map exists to resolve: **ambiguities** (a need more than one repo could provide — pick the
  winner) and **gaps** (a need no included repo provides — supply a default value, or add the
  provider).
- Selecting a map shows its repos, wiring and defaults, with **Edit** and **Delete**. Editing a map is
  always safe — a map is resolved at checkout, so changing it never invalidates a workspace you already
  built.
- **Check out a workspace** takes a slice of the map into a live workspace (see Workspaces).

### Workspaces
- **New workspace / check out**: pick a map, name the workspace, and take a slice. sprig makes the
  worktrees (parked in detached HEAD at the base — no branch until you claim it) and allocates the
  ports each selected repo provides.
- **Partial slices**: the form lists the map's repos, all ticked. Untick the ones you don't need this
  time and sprig leaves them out entirely — no worktree, no `.env`, no compose. A need whose provider
  you dropped is filled from the map's **default** if it has one, or reported as a gap before you
  commit.
- Selecting a workspace shows its per-repo detail (branch, worktree path, the resolved
  `${sprig.<cap>.<out>}` values, drift state) and a lifecycle toolbar:
  - **Up / Down / Reset** — docker infra (shown only when a selected repo actually declares infra).
  - **Reconcile** — *diagnose* worktree drift (read-only).
  - **Repair** — *fix* drift (prune stale registrations, remove orphaned folders).
  - **Open folder** — open the worktree.
  - **Remove** — tear down, with a confirm bar and an explicit "also delete the branch" checkbox.

When a downstream page is empty it points you back upstream — Maps with no repos offers **Add a
repo**, Workspaces with no map offers **Compose a map** — so you're never sent to a dead end.

### Settings
Pinned to the bottom of the nav. Controls how sprig picks host ports:
- **Port range** — the start/end (inclusive) of the range workspace ports are allocated from
  (default **8000–8999**). Changes apply to *new* workspaces; existing ones keep the ports they hold.
- **Restricted ports** — ports that are never allocated even when they fall inside the range (e.g.
  something else on the machine already owns them). One per line or comma-separated.
- **Check a port** — type a port to see its status: *Available*, *Restricted*, *In use* (by which
  workspace), or *Outside sprig's range*.
- **Ports in use** — every port currently leased to a workspace.
- **Changelog** — toggle whether the "what's new" window appears on the first launch after an update.

### About
Pinned to the bottom of the nav (with the running version shown alongside). Shows the installed
version, a **View changelog** link, links to the **source repo** and its **issue tracker**, and a
**Check for updates** button — when the release feed has a newer version, **Download & install**
applies it and restarts sprig.

---

## CLI

The CLI (`sprig`) drives the same engine. Run `sprig --help` for the full surface; add `--json` to
any read command for machine-readable output.

**Interactive by default.** Any command that needs a target — a workspace, a map, a repo — will ask
for it when you run the command bare at a terminal, and otherwise take it from the arguments. So
`sprig create` walks you through map → repos → name, while `sprig create feature-x --map web+api` just
runs. The same is true of `sprig rm`, `sprig cd`/`sprig path`, and the single-workspace verbs
(`up`/`down`/`reset`/`status`/`info`), which pick from a list when you omit the name. In a script, a
pipe, or CI (no terminal), these stay non-interactive and fail fast instead of blocking on a prompt;
`--ni` forces that same non-interactive behaviour even at a terminal, and `--json` implies it.

### 1. Register repos

```sh
sprig repo add C:\code\my-api               # name inferred from .sprig.json / folder
sprig repo add C:\code\my-frontend --name web
sprig repo ls                               # list registered repos
sprig repo rm web                           # unregister
```

Starting from scratch on a repo with no `.sprig.json` — `sprig init` infers **provides** from the
ports it detects (a listen/service port becomes a provided capability; a consumed URL is flagged as a
`need` to declare by hand):

```sh
sprig init --repo C:\code\my-api --print    # inspect + preview a proposed config
sprig init --repo C:\code\my-api --register # write .sprig.json and register in one step
```

### 2. Compose a map

Maps are authored in the app's **Maps** page (or shared as a file and imported). The CLI lists,
shows and imports them:

```sh
sprig map ls                        # list maps and their repos
sprig map show web+api              # dump one map (add --json for the raw record)
sprig map import C:\tmp\web+api.json  # validate + save a shared map
```

A map that just composes repos which wire cleanly by capability name needs nothing but its repo list.
Ambiguities (more than one provider) and gaps (no provider) are resolved with `wiring` / `defaults`
entries — see the [configuration reference](config-reference.md#map--mapsnamejson).

### 3. Check out / create a workspace

The pooled flow is recommended — a map backs a bounded set of reusable `<map>-<n>` workspaces:

```sh
sprig pool status web+api                          # the map's pool (in use / free / buildable)
sprig pool checkout web+api --branch feature-x     # take one, on a named branch
sprig pool release --workspace web+api-1           # hand it back (docker stop; disk kept)
```

Or create one directly:

```sh
sprig create                                  # at a terminal: pick map → repos → name
sprig create feature-x --map web+api          # from a map (multi-repo)
sprig create quick --repo C:\code\my-api      # a single repo, no map
```

Create prints the allocated ports and each repo's resolved values. If a repo needs a capability
nothing in the slice provides (and no map default fills it), create fails and names the gap:
`repo.module needs '<capability>'`.

Only want part of the map? Narrow it — the two flags are interchangeable, and both accept a
comma-separated list (or repeat the flag):

```sh
sprig create backend-only --map web+api --without web   # everything but web
sprig create backend-only --map web+api --only api      # the same thing, said the other way
```

A deselected repo is left out completely: no worktree, no `.env`, and its compose files are never
generated, so the workspace's infra is only what the kept repos declare. A need whose provider you
dropped is filled from the map's default, or reported as a gap.

```sh
sprig ls                      # all workspaces (repos, ports, status)
sprig info feature-x          # one workspace, in full: repos, ports, drift, live containers
sprig info                    # at a terminal: pick which workspace to inspect
```

Every workspace verb also accepts a `ws`/`workspace` prefix, so `sprig ws ls` and `sprig ws info
feature-x` are the same commands — handy if you prefer the noun-verb form used by `repo`/`map`.

**Jump into a workspace.** `sprig cd` opens a new terminal window already sitting in a workspace's repo
(or a module within it):

```sh
sprig cd feature-x            # picks any repo/module the name leaves ambiguous, then drops you in
sprig cd feature-x api web    # fully specified — straight in, no prompts
sprig cd                      # pick workspace → repo → module from scratch
sprig path feature-x api      # just print the directory — the one for scripts and shell wrappers
```

`sprig path` is the scripting counterpart: it resolves the same workspace/repo/module but prints the
directory instead of opening a window, so `Set-Location (sprig path feature-x)` (or `cd "$(sprig path
feature-x)"`) drops your *current* shell into it. Add `--json` for the structured target.

### 4. Run the infrastructure

Only for workspaces whose selected repos declare docker infra:

```sh
sprig up feature-x            # docker compose up (isolated project sprig-feature-x)
sprig status feature-x        # live container status only (info shows this too, plus everything else)
sprig down feature-x          # stop; keeps volumes
sprig down feature-x --volumes  # stop and wipe data
sprig reset feature-x         # down then up
```

Omit the workspace on any of these at a terminal (`sprig up`, `sprig status`, …) and you pick it from
a list.

### 5. Tear down

```sh
sprig rm                            # at a terminal: pick the workspace, then confirm
sprig rm feature-x                  # at a terminal: asks to confirm before tearing down
sprig rm feature-x --yes            # tears down infra + worktrees; keeps the claim branch
sprig rm feature-x --yes --force    # also deletes the claim branch (loses its commits)
```

At a terminal `rm` confirms before it destroys anything (and offers to delete the branch), so `--yes`
is only needed to skip that prompt — which is exactly what a script, a pipe, CI, or `--json` requires.
Teardown walks each layer independently and idempotently, so an interrupted teardown is resumable.

### 6. Settings

```sh
sprig settings                                   # show the port range and restricted ports
sprig settings set --start 8000 --end 9000       # the range sprig allocates from (end exclusive)
sprig settings set --restrict 8500 --unrestrict 8600   # never/again allocate specific ports
```

The same port-allocation policy the app's **Settings** screen edits; `--start`/`--end` set the
range, `--restrict`/`--unrestrict` add and remove individual ports.

---

## Drift and reconcile

A workspace's record is the source of truth for what *should* exist. If a worktree folder gets
deleted by hand, or git loses track of one, the record and reality drift apart.

```sh
sprig reconcile               # check every workspace (read-only)
sprig reconcile feature-x     # check one
sprig doctor                  # alias for reconcile over all workspaces
sprig reconcile --repair      # detect AND fix drift
```

Per-repo worktree states:

| State | Meaning | Repair action |
|---|---|---|
| `Healthy` | registered with git and present on disk | none |
| `MissingFolder` | git still tracks it, folder was deleted | prune the stale registration |
| `Orphaned` | folder exists, git dropped its registration | delete the folder, then prune |
| `Gone` | neither registered nor on disk | none (already cleaned up) |

`Repair` never touches `Healthy` worktrees. In the desktop app, **Reconcile** does the diagnosis and
**Repair** applies the fixes — same operations, surfaced as two buttons.

> Reconcile/repair is git-worktree hygiene only — it does not touch docker infra. Use
> `down`/`reset` for infrastructure.
