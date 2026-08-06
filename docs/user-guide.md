# User guide

This walks through the full sprig flow — register repos, define a stack, create an isolated
workspace, run its infra, and tear it down — from both the **desktop app** and the **CLI**. For
the file formats, see the [configuration reference](config-reference.md).

## Concepts in 30 seconds

- **Repo** — a git repo with a committed `.sprig.json` declaring the inputs it consumes.
- **Stack** — a named set of repos plus the ports and per-repo bindings that supply those inputs.
  Lives centrally, never in a repo.
- **Workspace** — a live instance of a stack: one git worktree per repo (on a `sprig--<workspace>`
  branch), a set of allocated ports, and optional docker infra.
- **Central store** — `%LOCALAPPDATA%\sprig`: the repo registry, stack definitions, and the
  per-workspace records that are the source of truth for what should exist.

---

## Desktop app

The app opens on **Home** and the left nav is grouped by what you're doing:
**Home**, then **Set up** (Repos, Stacks), then **Run** (Workspaces) — the order you actually work
in. Each page is a two-pane layout: a list on the left, detail on the right (the list caps at a
readable width, and the detail takes the rest on a wide window).

### Home
The front door. It shows where you are along the **repo → stack → workspace** pipeline as a journey
rail with live counts, plus a single **next best action** button that always points at the right
next step (add a repo → wire a stack → spin up a workspace). Clicking a rail tile jumps to that
page. Once you have workspaces, Home also lists your recent ones with quick actions. **How it
works** reveals the one-directional model (a stack supplies every value a repo declares).

First time? **Walk me through setup** starts a guided strip across the top that launches the real
Add-repo → Build-a-stack → Spin-up flows in order and auto-advances as you go, with **Back** and
**Skip**.

### Repos
- **Add repo** — browse to a git folder. If it already has a `.sprig.json` it's registered as-is;
  if not, sprig inspects the repo, proposes one, writes it, and registers — in one step.
- Selecting a repo shows its declared **inputs** (name + example) and, below them, a **tab per
  module** summarising where those inputs are used (that module's env / compose / setup). **Edit**
  opens an in-place editor for the `.sprig.json`; **Open in…** launches Explorer / VS Code / a terminal.
- A repo that declares **no inputs** offers **Isolate this repo** — spin up a workspace straight
  from it, no stack needed (the ad-hoc path).
- **Unregister selected** removes it from the registry (never touches the repo on disk).

#### Monorepos — one repo, many modules
A repo is made of **modules** — slices with their own `.env` files, docker compose files and setup
commands. A single-app repo has one module; a monorepo has several (e.g. `apps/web`, `apps/api`).
- **Inputs stay shared and sit at the top**, above the module tabs — so while editing any module you
  can see every input already declared (no duplicating a port per module) and the "variables you
  should add" hint spans all modules.
- In the editor, each module is a **tab**. Give it a **name** and an optional **path** (the
  subdirectory it lives in) — its env/compose paths resolve under that path, and its setup runs there.
- **+ Add module** adds a tab; **Delete module** removes one — all the way down to zero, so you can
  rebuild a repo's modules from scratch.
- Older single-app `.sprig.json` files are upgraded automatically: their env/compose/setup become one
  module named `app`, rewritten the next time you save.

### Stacks
- **New stack** opens the builder: name it (validated live — path-safe characters only, so you
  learn the rule as you type), tick the repos, add named **ports** (with a live preview), and bind
  each repo's **inputs** — a literal or a `${sprig.ports.<name>}` / `${sprig.workspace}` template
  with `${sprig.*}` autocomplete; each input's example shows underneath for easy copy-and-tweak.
- Selecting a stack shows its full configuration — repos, ports, and per-repo bindings — with
  **Edit** and **Remove**. Editing reuses the builder, pre-filled. **Edit is only offered when no
  workspaces were created from the stack** (otherwise it tells you how many use it): the workspaces
  you already built won't change if you edit the stack, so editing an in-use one would mislead.

### Workspaces
- **New workspace**: pick a stack, name the workspace, Create. sprig makes the worktrees, branches,
  and allocated ports.
- **Partial workspaces**: the create form lists the stack's repos, all ticked. Untick the ones you
  don't need this time and sprig leaves them out entirely — no worktree, no `.env`, and their compose
  files are ignored, so `up` only starts the infra of the repos you kept. Any stack port left with no
  consumer isn't provisioned either; the form names the ports you'll lose before you commit, and the
  workspace is badged **partial** afterwards. A port a repo you *kept* still references (a frontend's
  `apiUrl`, say) is provisioned as usual, even when the repo behind it is one you dropped.
- Selecting a workspace shows its per-repo detail (branch, worktree path, resolved inputs, drift
  state) and a lifecycle toolbar:
  - **Up / Down / Reset** — docker infra (shown only when the stack actually declares infra).
  - **Reconcile** — *diagnose* worktree drift (read-only).
  - **Repair** — *fix* drift (prune stale registrations, remove orphaned folders).
  - **Open folder** — open the worktree.
  - **Remove** — tear down, with a confirm bar and an explicit "also delete the branch" checkbox.

When a downstream page is empty it points you back upstream — Stacks with no repos offers **Add a
repo**, Workspaces with no stack offers **Build a stack** — so you're never sent to a dead end.

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

### 1. Register repos

```sh
sprig repo add C:\code\my-api               # name inferred from .sprig.json / folder
sprig repo add C:\code\my-frontend --name web
sprig repo ls                               # list registered repos
sprig repo rm web                           # unregister
```

Starting from scratch on a repo with no `.sprig.json`:

```sh
sprig init --repo C:\code\my-api --print    # inspect + preview a proposed config
sprig init --repo C:\code\my-api --register # write .sprig.json and register in one step
```

### 2. Define a stack

Ports are repeatable (`--port`), bindings are repeatable (`--bind repo:input=expr`):

```sh
sprig stack create web+api --repos my-frontend,my-api \
  --port frontend_port --port api_port --port postgres_port \
  --bind my-frontend:frontend=${sprig.ports.frontend_port} \
  --bind my-frontend:apiUrl=http://localhost:${sprig.ports.api_port} \
  --bind my-api:port=${sprig.ports.api_port} \
  --bind my-api:dbPort=${sprig.ports.postgres_port}

sprig stack ls                # list stacks
sprig stack show web+api      # dump one stack's JSON
sprig stack rm web+api
sprig stack export web+api C:\tmp\web+api.json   # share via file
sprig stack import C:\tmp\web+api.json
sprig templates               # stacks + their repos
```

### 3. Create a workspace

```sh
sprig create feature-x --stack web+api        # from a stack (multi-repo)
sprig create quick --repo C:\code\my-api      # ad-hoc, single repo, no stack
```

Create prints the allocated ports and each repo's resolved inputs. If a repo declares an input the
stack doesn't bind, create fails and names the missing repo + input + example.

Only need part of the stack? Narrow it — the two flags are interchangeable, and both accept a
comma-separated list (or repeat the flag):

```sh
sprig create backend-only --stack web+api --without web   # everything but web
sprig create backend-only --stack web+api --only api      # the same thing, said the other way
```

A deselected repo is left out completely: no worktree, no `.env`, and its compose files are never
generated, so the workspace's infra is only what the kept repos declare. Stack ports left with no
consumer aren't provisioned — create and `sprig info` list them under `ports not provisioned`, and
they stay free for other workspaces. A port a kept repo still references is provisioned as normal.

```sh
sprig ls                      # all workspaces (repos, ports, status)
sprig info feature-x          # one workspace's repos, ports, and drift state
```

### 4. Run the infrastructure

Only for workspaces whose repos declare docker infra:

```sh
sprig up feature-x            # docker compose up (isolated project sprig-feature-x)
sprig status feature-x        # live container status
sprig down feature-x          # stop; keeps volumes
sprig down feature-x --volumes  # stop and wipe data
sprig reset feature-x         # down then up
```

### 5. Tear down

```sh
sprig rm feature-x --yes            # tears down infra + worktrees; keeps the sprig branch
sprig rm feature-x --yes --force    # also deletes the sprig--feature-x branch (loses its commits)
```

Teardown walks each layer independently and idempotently, so an interrupted teardown is resumable.

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
