# User guide

This walks through the full sprig flow — register repos, define a stack, create an isolated
workspace, run its infra, and tear it down — from both the **desktop app** and the **CLI**. For
the file formats, see the [configuration reference](config-reference.md).

## Concepts in 30 seconds

- **Repo** — a git repo with a committed `.sprig.json` declaring the inputs it consumes.
- **Stack** — a named set of repos plus the ports and per-repo bindings that supply those inputs.
  Lives centrally, never in a repo.
- **Workspace** — a live instance of a stack: one git worktree per repo (on a `sprig/<workspace>`
  branch), a set of allocated ports, and optional docker infra.
- **Central store** — `%LOCALAPPDATA%\sprig`: the repo registry, stack definitions, and the
  per-workspace records that are the source of truth for what should exist.

---

## Desktop app

The app has three areas in the left nav.

### Repos
- **Register** an existing repo (one that already has a `.sprig.json`), or **Init & register** to
  have sprig inspect the repo, propose a `.sprig.json`, write it, and register in one step.
- Selecting a repo shows its declared **inputs** (name + example) — the values a stack must supply.
- **Unregister** removes it from the registry (never touches the repo on disk).

### Stacks
- **New stack** (top button) opens a modal: name it, tick the repos, add named **ports**, and for
  each repo fill in its **inputs** — each input is auto-listed with its example hint, and you type a
  literal or a `${sprig.ports.<name>}` template.
- Existing stacks show their repos and ports. **Remove selected** deletes a stack.

### Workspaces
- **New workspace** (top button): pick a stack, name the workspace, Create. sprig makes the
  worktrees, branches, and allocated ports.
- Selecting a workspace shows its per-repo detail (branch, worktree path, resolved inputs) and a
  lifecycle toolbar:
  - **Up / Down / Reset** — docker infra (shown only when the stack actually declares infra).
  - **Reconcile** — *diagnose* worktree drift (read-only).
  - **Repair** — *fix* drift (prune stale registrations, remove orphaned folders).
  - **Open folder** — open the worktree.
  - **Remove** — tear down, with a confirm bar and an explicit "also delete the branch" checkbox.

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
sprig rm feature-x --yes            # tears down infra + worktrees; keeps the sprig/ branch
sprig rm feature-x --yes --force    # also deletes the sprig/feature-x branch (loses its commits)
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
