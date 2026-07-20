# sprig

**Worktree + infrastructure isolation for any git repo.**

sprig lets you spin up an isolated copy of one or more repos — each on its own git
*worktree*, its own branch, its own non-colliding ports, and its own docker infrastructure —
so you can work on several things in parallel without them stepping on each other. Your source
repos are never mutated; everything sprig generates lives in a central store or in the
throwaway worktree.

- **Isolated worktrees** — each workspace gets a `<repo>--<workspace>` worktree on a fresh
  `sprig/<workspace>` branch, off your current `HEAD`. Your main checkout is untouched.
- **Non-colliding ports** — named ports are allocated per workspace, so two workspaces of the
  same stack run side by side without port clashes.
- **Isolated docker infra** — sprig generates a per-workspace compose file (project name
  `sprig-<workspace>`) so containers, networks, and volumes don't collide.
- **One-directional config** — a repo declares only the *inputs* it needs; a *stack* supplies
  every value. Easy to trace: values flow one way, stack → repo.
- **Drift-safe** — `reconcile`/`doctor` detects and repairs record-vs-reality drift (a deleted
  or orphaned worktree), so a half-cleaned-up workspace is always recoverable.

## The model in one picture

```
Repo (.sprig.json, committed in the repo)      Stack (central store, never in a repo)
  = CONSUMER                                      = PRODUCER
  declares inputs it needs        <── binds ──    owns named ports (auto-allocated)
  env/compose templates reference                 supplies each repo's inputs via
  ${sprig.<input>} and ${sprig.workspace}         bindings[repo][input] = expression
```

Every value originates in the stack and flows one way into a repo. A repo never produces values
for another repo — the stack wires them together.

## Quick start (CLI)

```sh
# 1. Register the repos you want to isolate (reads each repo's committed .sprig.json)
sprig repo add C:\code\my-frontend
sprig repo add C:\code\my-api

# 2. Define a stack that wires them together
sprig stack create web+api --repos my-frontend,my-api \
  --port api_port \
  --bind my-api:port=${sprig.ports.api_port} \
  --bind my-frontend:apiUrl=http://localhost:${sprig.ports.api_port}

# 3. Create an isolated workspace from the stack
sprig create feature-x --stack web+api      # worktrees + branches + allocated ports

# 4. Bring its docker infra up, work, then tear it down
sprig up feature-x
sprig down feature-x                          # keeps volumes; add --volumes to wipe
sprig rm feature-x --yes                      # tears down; add --force to also delete the branch
```

Don't have a `.sprig.json` yet? `sprig init --repo C:\code\my-api --print` inspects the repo and
proposes one.

## Desktop app

`Sprig.App` is an Avalonia (dark-mode) desktop head over the same engine. It has three areas —
**Repos** (register / init), **Stacks** (builder), and **Workspaces** (create + per-workspace
lifecycle with a drift/reconcile surface). Everything the CLI does is reachable from the UI.

## Where state lives

| Location | Contents |
|---|---|
| **Source repo** (tracked) | `.sprig.json` only — the single file sprig adds to your repo |
| **Worktree** `<repo>--<workspace>` (sibling dir) | clobbered `.env.*` files; dies with the worktree |
| **Central store** `%LOCALAPPDATA%\sprig` | repo registry, stack definitions, per-workspace records, allocated ports, generated compose files |

## Docs

- **[Configuration reference](docs/config-reference.md)** — the `.sprig.json` and stack schemas.
- **[User guide](docs/user-guide.md)** — end-to-end walkthrough for the UI and the CLI.
- **[Packaging & updates](docs/packaging.md)** — building the installer and the update-notify flow.

## Requirements

- .NET 10 SDK
- git (on `PATH`)
- Docker Desktop / docker compose — only needed for workspaces whose repos declare infra.

Windows-first; the engine (`Sprig.Core`) is kept OS-agnostic.
