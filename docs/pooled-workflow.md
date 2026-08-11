# The pooled workflow

Sprig gives you a **pool** of reusable, isolated workspaces built from a **stack**. You
*check one out* to work in, and *release* it when you're done — the pool is bounded, so
you never end up with floating instances forever. This is the way to get a workspace.

## The three nouns

| Noun | What it is |
|---|---|
| **Stack** | The recipe: which repos, how they're wired (ports + input bindings), the setup that stands them up, and a `maxSlots` ceiling. Lives centrally, referenced by name. |
| **Pool** | The bounded, live set of workspaces for a stack — capped at the stack's `maxSlots`. Not a thing you create; it's just the workspaces that exist for the stack. |
| **Workspace** | One isolated environment. Either **claimed** (you're using it) or **unclaimed** (free to take — though not necessarily clean). |

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
sprig pool checkout suite      # take a workspace, label it, choose how it's handled
#   … work in it …
sprig pool release            # hand it back: docker down, nothing removed from disk
sprig pool status suite       # see the pool: claimed / free / degraded
```

**Checkout** either reuses an unclaimed workspace or lazily builds a new `suite-<n>` (up
to `maxSlots`); past the cap it refuses until you release one. You give it a **label**
(just a name to recognise it by) and pick how an existing workspace is handled:

| Mode | Git | node_modules / build | Docker volumes |
|---|---|---|---|
| **as-is** | untouched — resume where you left off | kept | kept |
| **fresh** | every repo reset to its base branch | kept (setup reconciles) | **wiped** (clean DB) |
| **refresh `repo,…`** | only the named repos reset to base | kept | kept |

Flags: `--label`, `--new`, `--workspace <name>`, `--fresh` / `--as-is` / `--refresh <repos>`,
`--force` (discard commits the base lacks). Omit them at a terminal to be prompted.

**Release** is cheap and safe: it stops the containers (frees CPU/RAM) and marks the
workspace unclaimed, but **removes nothing from disk** — worktrees, branches, installed
deps and volumes all stay. So a later **as-is** checkout resumes in seconds, and a
mis-release is recovered by re-claiming the same workspace as-is.

## Recycling a live workspace directly

`sprig ws refresh <workspace> [--only repo,…] [--force]` resyncs a workspace's repos to
their base branch **without** re-downloading dependencies (it hard-resets tracked files
only). This is what `fresh`/`refresh` checkout use under the hood; run it directly to
clean up a workspace you're keeping claimed.

`sprig ws restart <workspace>` bounces its docker infra (down/up, volumes kept).

## Degraded workspaces

Setup runs on every checkout and refresh, so a broken setup can't hide: a workspace whose
last setup failed is flagged **degraded** in `pool status` and at checkout. It stood up,
but finish its setup before relying on it.

## Note: `sprig ws create` is being retired

The older `sprig ws create` (create a one-off workspace) still works, but sprig now
favours the pooled flow above. For a single repo, register it, make a one-repo stack, and
`pool checkout` it. A first-class one-off form may return if there's demand.
