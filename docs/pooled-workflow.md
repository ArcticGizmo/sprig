# The pooled workflow

Sprig gives you a **pool** of reusable, isolated workspaces built from a **stack**. You
*check one out* to work in, and *release* it when you're done — the pool is bounded, so
you never end up with floating instances forever. This is the way to get a workspace.

## The three nouns

| Noun | What it is |
|---|---|
| **Stack** | The recipe: which repos, how they're wired (ports + input bindings), the setup that stands them up, and a `maxSlots` ceiling. Lives centrally, referenced by name. |
| **Pool** | The bounded, live set of workspaces for a stack — capped at the stack's `maxSlots`. Not a thing you create; it's just the workspaces that exist for the stack. |
| **Workspace** | One isolated environment. Either **claimed** (you're using it, on a branch you named) or **unclaimed** (free to take — though not necessarily clean). |

An **idle** workspace has no branch of its own: its worktrees park in **detached HEAD** at
the stack's base, so any number of workspaces can sit at the same commit without git
complaining. Identity attaches when you claim it — and the thing that carries it is a real
git **branch** you name, cut across every repo in the stack. The label is just an optional
sticky note.

## One-time setup

```sh
sprig repo add <path>                         # register each repo (needs a .sprig.json, even a name-only one)
sprig stack create suite --repos a,b,c \      # define the stack
    --max-slots 4 \                           # at most 4 workspaces at once
    --setup a:"npm ci" --setup b:"dotnet restore"
```

A repo can be pooled with a **name-only `.sprig.json`** — the stack can carry its setup
(`--setup <repo>:<command>`, repeatable). Put the effort into the stack; the repo stays
thin.

## The loop

```sh
sprig pool checkout suite --branch feature-x   # claim one, cutting branch feature-x across the stack
#   … work in it …
sprig pool release --workspace suite-1         # hand it back: docker down, nothing removed from disk
sprig pool status suite                        # see the pool: claimed / free / degraded
```

**Checkout** either reuses an unclaimed workspace or lazily builds a new `suite-<n>` (up to
`maxSlots`); past the cap it refuses until you release one. Claiming is **two independent
choices** — the branch's **start point** (git) and how the warm **environment** is handled
(disk/docker):

- A **branch name** (`--branch`, required) — the workspace's identity, cut across every
  repo in the stack. A name that already exists **locally** in any repo is a hard block
  (reported and aborted, nothing half-created); a name that exists only on a **remote** is
  a warning, and the local branch is cut anyway.
- An optional **label** (`--label`) — free text to recognise the checkout by; git never
  sees it.
- A **start point** — where the new branch begins (see below); defaults to the stack's
  base.
- **keep** or **fresh** — how an existing workspace's warm state is handled:

| Mode | Git | node_modules / build | Docker volumes |
|---|---|---|---|
| **keep** *(default)* | clean branch cut from the start point; tracked files reset to it | **kept** (no reinstall — fast) | **kept** |
| **fresh** | same clean branch from the start point | **reinstalled** (setup re-runs) | **wiped** (clean DB) |

Both modes cut a clean branch from a *known* start point and reset tracked source to it —
gitignored artifacts (node_modules, docker volumes, a real `.env`) always survive. "keep"
means *keep what's expensive on disk*, **not** *keep my uncommitted edits*: those are reset
to the start point (and were reported to you at the previous release). keep vs fresh differ
**only** in that warm state.

Flags: `--branch <name>` (required), `--label <label>`, `--from <ref>`, `--keep` / `--fresh`,
`--new` (force a brand-new workspace), `--workspace <name>` (reuse a specific one),
`-i`/`--interactive` and `--ni`/`--no-interactive`. (`--force` is reserved and currently a
no-op: the previous branch is always retained, so no claim ever discards commits.) Omit the
flags at a terminal to be prompted for each.

### Choosing the start point

The start point defaults to **each repo's base**, and sprig **prefers an `upstream` remote
over `origin`** — the fork / gitflow case where you branch from the canonical repo but push
to your fork, so `origin/main` is stale. Both create and checkout **fetch first**, so the
base is current.

Override it with `--from <ref>` (e.g. `--from upstream/main`), or, at a terminal, pick from
an interactive list of the connected remotes' branches (default first). In the **desktop
app** the same choice is a searchable dropdown — ranked *current → default → recent*, with
chips for main/master and your current branch — plus a **branch graph**: a branch icon
opens a GitKraken-style commit-DAG overlay with the current branch ringed; click a branch
or commit to set the start point. The start point is **one ref across the whole stack**; a
repo that doesn't have it falls back to that repo's base.

**Release** is cheap, safe, and **touches no git at all**: it stops the containers (frees
CPU/RAM) and marks the workspace unclaimed, but **removes nothing from disk** — worktrees,
the claim branch, installed deps and volumes all stay. The workspace sits on its (unique)
claim branch until the next checkout cuts a fresh one. A mis-release is recovered by
re-claiming the same workspace with **keep**.

Because release never resets anything, it **reports pending work** so nothing is silently
stranded before a later checkout resets the tree — per repo:

- **Uncommitted changes** — a dirty working tree.
- **Unpushed commits** — commits on the claim branch that aren't on any remote.

e.g. *"repo A: uncommitted changes · repo B: 2 unpushed commits"*. The branch ref survives
release, so unpushed commits are stranded-but-recoverable — but you should know before a
later **fresh** wipes the workspace.

## Recycling a live workspace directly

`sprig ws refresh <workspace> [--only repo,…] [--force]` resyncs a workspace's repos to
their base branch **without** re-downloading dependencies (it hard-resets tracked files
only). Run it directly to clean up a workspace you're keeping claimed, without releasing and
re-checking-out.

`sprig ws restart <workspace>` bounces its docker infra (down/up, volumes kept).

## Degraded workspaces

Setup runs on every checkout (in **fresh** mode) and refresh, so a broken setup can't hide:
a workspace whose last setup failed is flagged **degraded** in `pool status` and at
checkout. It stood up, but finish its setup before relying on it.

## Migration from the old model

Workspaces created under the older `sprig--<workspace>` branch-per-workspace scheme are
**not migrated in place**. Tear them down (`sprig ws rm --force`) and re-check-out from the
pool to get the detached-workspace / branch-on-claim behaviour.

## Note: `sprig ws create` is being retired

The older `sprig ws create` (create a one-off workspace) still works, but sprig now
favours the pooled flow above. For a single repo, register it, make a one-repo stack, and
`pool checkout` it. A first-class one-off form may return if there's demand.
