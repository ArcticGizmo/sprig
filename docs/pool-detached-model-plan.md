# Pool → Detached-Slot / Branch-on-Claim Model

**Status:** Design agreed 2026-08-12 · supersedes the branch-per-workspace scheme in
`docs/pool-model-plan.md` (M1–M5). Implementation on the `pools` branch.

## Why this exists

The old model gave every workspace a synthetic identity it never asked for: an
auto-numbered name (`<stack>-<n>`), a worktree folder, a branch (`sprig--<workspace>`),
**and** a free-text `Label` that git never saw. Four names for one thing, and the one the
human actually used — the label — floated free of anything git or the filesystem could
show you. Reset logic had to keep those names in sync, which is where the "fresh reset"
friction came from.

The reframe: **an idle workspace is an anonymous, pre-warmed slot with no branch of its
own. Identity attaches at _claim_ time, and the thing that carries it is a real git
branch** — because that is exactly what a worktree is built to hold. The label drops to an
optional readability aid.

## The core realisation

A worktree holds **one** branch (or a detached HEAD) at a time, and git forbids the same
branch being checked out in two worktrees at once. So:

- A pool has N slots per repo → N worktrees of that repo. They **cannot** all sit on
  `main`. A per-slot "home branch" would satisfy that constraint, but it never diverges
  from `main` (nothing commits to it), so it's a redundant alias.
- **Detached HEAD** solves it cleanly: a worktree parked in detached HEAD at `origin/main`
  isn't "checking out a branch", so any number of idle slots can park at the same commit.
  No home branch needed.

The warm value of the pool — the registered worktree, docker volumes, `node_modules`,
`.env` — all lives in the **folder + docker**, keyed to the stable slot name
`<stack>-<slot>`. None of it depends on a branch. So an idle slot carries no branch, and
`origin/main` *is* the base every claim branches from.

## Worktrees, not full clones (confirmed)

Kept worktrees over per-slot isolated clones:

- The cost we pool to avoid is **infra re-init** (docker, seeds, `node_modules`), which is
  identical either way. On the git axis, worktrees are strictly cheaper: shared object
  store, cheap creation, one `fetch` refreshes every slot's `origin/main`.
- Working trees are already isolated per worktree (HEAD/index/tree are per-worktree). The
  shared **ref namespace** is a *feature* here — it's what makes the cross-repo
  branch-name conflict check natural. N independent clones would each have a private branch
  namespace, hiding collisions.
- The one sharp edge — ghost admin entries when a folder is deleted without
  `git worktree remove` — is handled by `git worktree prune`, and detached parking removes
  the branch-in-use half of it.

## The three identities

| Identity | Value | Lifecycle |
|---|---|---|
| **Slot** — worktree folder + docker/compose project | `<repo>--<stack>-<slot>` / `sprig-<stack>-<slot>` | stable for the life of the slot; machine-named |
| **Branch** — the work | user-chosen at claim, spans all repos in the stack | created on claim, kept on release |
| **Label** — the sticky note | optional free text | cosmetic; recognition only |

## State table (per repo / worktree)

Claiming is **two independent choices**: the branch's **start point** (git) and how the warm **environment**
is handled (disk/docker). Pulling these apart is what makes "keep" predictable — the new branch always
starts from a *known* ref, never from "whatever the slot happened to be parked on".

| Event | git action |
|---|---|
| **Slot created** | `git worktree add <path> --detach <base>` (base resolved via existing `ResolveDefaultBase`) — a *freshly-created* slot parks in detached HEAD (N slots can't all sit on `main`) |
| **Claim — new slot** | `git switch -c <branch>` from base (the slot is already at base; env/compose/setup already done by create) + start infra — the minimal `CutBranchAndStart` path |
| **Claim — keep** (reuse, default) | `git fetch`; `git switch -c <branch>` then `git reset --hard <startPoint>` (default base); reapply env/compose; **keep** deps + volumes (no reinstall); start infra |
| **Claim — fresh** (reuse) | same git as keep, but **reinstall deps (setup)** and **wipe volumes** |
| **Release** | report pending work — **touch no git at all**. The worktree stays on its claim branch; nothing is detached or reset. |
| **Idle after release** | on its (unique) claim branch — no conflict, because each released slot has its own branch name |

**keep vs fresh** differ *only* in the warm state: both cut a clean branch from the start point and reset
tracked source to it (gitignored artifacts — node_modules, docker volumes, real `.env` — always survive).
keep leaves deps + volumes untouched (fast); fresh reinstalls deps and wipes volumes (clean). "keep" means
*keep what's expensive on disk*, **not** *keep my uncommitted source edits* — those are reset to the start
point (and were already reported at release).

### Start point (default prefers upstream; searchable picker)

The **start point** defaults to each repo's base, and `ResolveDefaultBase` now **prefers an `upstream`
remote over `origin`** — the fork/gitflow case where you branch from the canonical repo but push to your
fork, so `origin/main` is stale. Slot creation and claim both **fetch first**, so the base is current.

Override it with a **branch picker**: `Claim` takes a `startPoint` (a single ref applied to every repo),
surfaced as `--from <ref>` and an interactive list in the CLI, and a searchable dropdown in the app —
fetch-populated, ranked *current → default → recent*, with chips for main/master and your current branch,
recent-by-default and full-search-on-type (`StartPointFilter`). A chosen ref absent from a repo falls back
to that repo's base (noted on the row).

There's also a **visual branch graph** (GitKraken-style): a branch icon by the dropdown opens an overlay
that draws the commit DAG in swimlanes (`git log --all` → `CommitGraphLayout` → a custom `GraphLinesControl`),
current branch ringed, with the searchable dropdown on top to jump to a specific branch. Click a branch pill
or a commit to set the start point. It reads the **first repo** of the stack (the graph is per-repo; the
start point still spans the stack).

> **Circle back (advanced, per-repo start point).** `startPoint` is currently one ref for the whole stack.
> Decide later whether to offer a *different* start point per repo (e.g. repo A from `origin/main`, repo B
> from `origin/release-2`), and how to resolve a ref that exists in some repos but not others. Tracked as a
> `TODO` on `WorkspaceService.Claim`.

> **Why release touches no git.** Detached parking exists only to solve the *freshly-created* case where many slots would otherwise want `main`. Once a slot has been claimed it owns a **unique** branch, so it can sit on that branch between uses with no conflict — and leaving it there is the only way release can be truly report-only (a `switch --detach` would fail on an uncommitted tree, i.e. it would have to *act*). The next claim always cuts a new branch from the chosen start point (default base), so git state is resolved at claim time, never at release.

## Claim: conflict pre-flight (atomic across the stack)

Claiming asks for a **branch name** (required) and an **optional label**. Before creating
anything:

1. Check the branch name against **every** repo in the stack.
2. A name is **blocked** if, in any repo, a local branch of that name already exists **or**
   it is checked out in another worktree. Report **all** impacted repos and abort — do
   nothing about it (the user resolves; the conflict may be more complex than we can safely
   automate).
3. A name that exists **only on the remote** (`origin/<branch>`) is a **warning**, not a
   block.
4. Create the branch in **no** repo until **all** are clear — never leave a half-claim.

## Release: report, don't act

On release, per repo, surface two distinct categories of pending work and act on neither:

- **Uncommitted work** — dirty working tree (`git status --porcelain` non-empty).
- **Unpushed commits** — the claim branch has commits not on any remote.

e.g. *"repo A: 3 uncommitted files · repo B: 2 unpushed commits"*. The branch ref survives
release, so unpushed commits are stranded-but-recoverable, not lost — but the user should
know before a later `fresh` resets the slot.

## Data-model changes (`InstanceRecord` / `InstanceRepo`)

- `InstanceRepo.Branch` becomes the **claim branch** — `null` while the slot is parked
  (detached), set to the user's branch name on claim, retained on release.
- Add a workspace-level **`Branch`** (the single claim-branch name that spans the stack's
  repos) so the UI/CLI has one name to show; `InstanceRepo.Branch` mirrors it per repo.
- `Label` stays but becomes optional (drop the "a checkout needs a label" guard).
- Name validation tightened from the `^[A-Za-z0-9._-]+$` regex to real git ref rules
  (`git check-ref-format`) for both the slot name and the user's branch name.

## Scope for the first cut

- **Drop per-repo refresh** (`CheckoutMode.Refresh` / `onlyRepos`) for now — it multiplies
  test states for little near-term value. Whole-stack **keep** / **fresh** only.
- Consider surfacing **"base is N commits behind origin/main"** on the claim dialog so the
  keep/fresh choice is informed rather than a coin flip. (Nice-to-have, not blocking.)

## Migration

Existing workspaces carry `sprig--<workspace>` branches and no detached-slot concept.
This is personal tooling on the `pools` branch — plan is **new model only**: existing
pooled workspaces should be town down (`sprig ws rm --force`) and re-created, rather than
migrated in place.

## Implementation phases

1. **Git layer** — `AddWorktreeDetached`, `CreateBranch`/`SwitchNewBranch`, `DetachTo`,
   `HasUncommittedChanges`, `CountUnpushedCommits`, `IsCheckedOutElsewhere`, ref-format
   validation. Unit-test against the process-runner fake.
2. **WorkspaceService** — `Create` parks worktrees detached (no branch); new `Claim`
   (conflict pre-flight → branch across repos → keep/fresh infra) and `Release` (report
   pending → detach). Retire `BranchFor`.
3. **PoolService** — `Checkout` takes `(branchName, label?)`; wire the conflict pre-flight
   and keep/fresh; `Release` returns the pending-work report.
4. **Records + store** — the `Branch`/`Label` shape above.
5. **CLI** — `pool checkout` gains `--branch` (required) and `--label` (optional); release
   prints the pending-work report.
6. **UI** — checkout dialog: branch (required) + label (optional); release surfaces pending
   work.
