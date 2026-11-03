# Sprig → Pool Model: Phased Implementation Plan

> **⚠️ Superseded (history).** The checkout model described here — `sprig--<workspace>` branches and
> the **as-is / fresh / refresh** handling modes — has been replaced by the detached-workspace /
> branch-on-claim model: idle workspaces park in detached HEAD, claiming cuts a user-named branch, and
> handling is **keep / fresh** only. See `docs/pool-detached-model-plan.md` for the current design and
> `docs/pooled-workflow.md` for the user-facing guide. This doc is kept as a record of the M1–M5 work.

**Status:** M1–M5 implemented on the `pools` branch · **Date:** 2026-08-11

> **Implementation status:** M1 (refresh-to-master), M2 (stack capacity / implicit pool),
> M3 (checkout / release), M4 (stack-carried setup + degraded surfacing) and M5 (everything
> pooled; `ws create` deprecated) are all built and tested. The user-facing guide is
> `docs/pooled-workflow.md`. Deferred: shared-running dependency resolution (only if the
> "bleed" actually bites).

Reframes sprig from a *pure worktree handler* (each workspace created ad-hoc, tied to
whatever you're building) into a **bounded pool of reusable, isolated workspaces**. You
*check out* a workspace (label it, choose how it should be handled), work in it, then
*release* it — which just marks it free and stops its infra, **leaving everything on
disk**. Workspaces are interchangeable environments, not feature branches.

Target UX:

```
sprig pool checkout "product-suite"      # take an unclaimed workspace (up to the stack's cap); label it; choose fresh / as-is / refresh
sprig pool release                       # mark a claimed workspace unclaimed; docker down; nothing removed from disk
```

> **Design decisions locked in (2026-08-11):**
> 1. **No explicit `pool create` step.** The concurrency ceiling (`maxSlots`) is a
>    property of the **stack**; checkout allocates workspaces lazily up to it (§1a). A
>    "pool" is the *emergent live set of workspaces for a stack*, not a provisioned object.
> 2. **Release never touches disk; claim decides state.** Release flips a workspace to
>    *unclaimed* and `docker compose down` (no `--volumes`) to free CPU/RAM. The expensive
>    artifacts (node_modules, build output, volumes) stay. When you next claim a workspace
>    you pick how to handle it — **fresh**, **as-is**, or **refresh some repos** — so a
>    99%-ready checkout can be reused in seconds (§3a).
> 3. **Keep the noun "stack" for the recipe.** No `Blueprint` rename — it only surfaces at
>    `pool checkout`, so the rebrand isn't worth the churn (§1).
> 4. **No repo-level `dependsOn`.** Abandoned — more pain than gain (§4a). The effort goes
>    into the stack + its setup, not new cross-repo dependency wiring.
> 5. **Everything is pooled.** One way to get a workspace: `pool checkout`. The ad-hoc
>    one-off `create` path is folded in; revisit a standalone form only on real demand (M5).

---

## 1. Vocabulary

No renames — two of the three nouns already exist; only **Pool** is new, and it's
implicit (nothing to persist).

| Noun | Meaning | Today |
|---|---|---|
| **Stack** | The recipe: which repos, how they're wired, the setup commands that stand them up, and a `maxSlots` concurrency ceiling. A "complete block of repos". | `StackDefinition` (+ `MaxSlots`) |
| **Pool** | The **emergent** live set of workspaces for a stack — not a provisioned object. Bounded by the stack's `maxSlots`; the cap is the whole point — no floating instances forever. | *implicit* |
| **Workspace** | One isolated environment for a stack. State is **claimed** or **unclaimed** (unclaimed ≠ clean); carries a user Label while claimed. | `InstanceRecord` (+ state fields) |

(We keep **workspace** rather than inventing "slot" — it's warmer and already the
codebase's word: `WorkspaceService`, the `ws` commands, `InstanceRecord.Workspace`.)

`sprig pool checkout "product-suite"` reads: *take an unclaimed workspace from the
"product-suite" pool* (creating one lazily if room remains under `maxSlots`).

### 1a. Why no explicit `pool create` step

Considered and rejected. Because workspaces are **lazy** (checkout materializes, release
just frees), a `pool create "stack" 6` command would do nothing but write a JSON file and
reserve port ranges — ceremony. Every benefit survives without it:

- **Cap enforcement** — checkout refuses workspace N+1 whether the ceiling lives on a Pool
  record or the stack. Identical.
- **Port-budget validation** — moves to stack-save time (`maxSlots × ports` must fit the
  range), plus a just-in-time check at checkout. Arguably better.
- **Status** — the live workspace set is derivable from instances tagged with the stack;
  no Pool object needed.

More importantly, an explicit Pool object inserts a **second stateful thing** between
stack and workspaces — a size that can drift from the recipe, another file to migrate and
reconcile. Folding the cap onto the stack preserves sprig's existing invariant that the
recipe is the single source of truth.

**Escape hatches, if ever needed (all YAGNI now):** an *optional* `--name` for multiple
pools off one stack; an *optional* `sprig pool warm "stack" 3` to pre-build workspaces for
instant checkout. Both layer on without disturbing the default implicit flow. The one case
that genuinely wants eager provisioning — a shared, always-warm server farm — is a
different product (team infra, not local worktrees).

**Note on what `maxSlots` represents:** concurrent-environment capacity is mostly a
*machine* limit (RAM/ports/disk), not a recipe fact. `maxSlots` is a sensible default
ceiling on the stack, and should be overridable by a future machine-global setting.

---

## 2. Where this lands in the code

- **CLI surface:** `src/Sprig.Cli/CliApp.cs` `Configure()` — add a `pool` branch beside
  `repo`/`stack`/`ws`. New `src/Sprig.Cli/Commands/PoolCommands.cs`.
- **Engine:** `src/Sprig.Core/Workspaces/WorkspaceService.cs` gains the workspace handling
  operations (refresh-to-master, per-repo refresh, resume). New
  `src/Sprig.Core/Pools/PoolService.cs` — a thin query/allocation layer over
  `InstanceStore` + the stack's `maxSlots`. **No `Pool` record, no `PoolStore`, no
  `pools/` directory** (§1a).
- **Recipe:** `StackDefinition` (`src/Sprig.Core/Stacks/StackDefinition.cs`) gains
  `MaxSlots`. No rename.
- **Persistence:** `InstanceRecord` (`src/Sprig.Core/Store/InstanceRecord.cs`) gains state
  fields (claimed/unclaimed, label, stack, workspace index). Reuses the existing
  `instances/<workspace>/instance.json`.
- **Ports:** `FilePortStore` allocates a disjoint port set per workspace and validates the
  `maxSlots` budget.
- **GUI parity:** `Sprig.App` (Avalonia) mirrors the pool lifecycle after the CLI lands.

The existing `ws reset` (`WorkspaceCommands.cs:549` → `WorkspaceService.Reset()`) is only
`down`+`up` of docker infra. It gets renamed to `ws restart`; the name is freed for the
real handling operations (refresh-to-master).

---

## 3. Milestones

Each milestone is independently shippable and testable. M1 is deliberately the
falsification probe — if recyclable workspaces don't work cleanly, the whole model is in
doubt and we learn it before building the pool machinery on top.

### M1 — Workspace refresh-to-master — *the probe*

**Goal:** prove a single workspace can be *resynced to master* and reused **without
throwing away the expensive disk artifacts** (node_modules, build output). This is the
load-bearing mechanic; "reset" here is a **git** operation, not a disk wipe (§4).

**Changes**
- Rename infra bounce: `ws reset` → `ws restart` (`WorkspaceService.Reset` →
  `RestartInfra`). Keep `reset` as an alias that warns, for one release.
- New `WorkspaceService.RefreshToBase(workspace, repos?, progress)`:
  1. **Safety guard** — if a repo's branch (`sprig--<name>`) has commits not merged into /
     pushed to its base, warn and require `--force` before discarding them. Work is never
     lost silently.
  2. `git fetch` the base ref, then hard-resync `sprig--<name>` to `origin/<base>`
     (**tracked files only** — this deliberately does *not* touch gitignored node_modules,
     build output, or real `.env` secrets).
  3. Regenerate only sprig-managed files (compose, the clobbered env block); re-seed env
     from source per `EnvClobberService`.
  4. Re-run the stack's setup commands — idempotent installers (`npm ci`, etc.) reconcile
     deps incrementally against the preserved `node_modules`.
  5. Restart infra (`down` then `up`; volumes kept by default — see §4 for the wipe
     decision).
- New `Base` concept: per-stack default (`origin/HEAD`/`origin/main`), per-repo override.

**Deliverable:** `sprig ws refresh <name> [--only repo,…]` resyncs a live workspace to
master, keeping installed deps.

**Exit criteria:** refresh a workspace across two different "features"; verify tracked
files match master, `node_modules` was *not* re-downloaded from scratch, infra healthy,
setup re-ran. Guard warns before discarding un-pushed commits.

---

### M2 — Stack capacity & implicit pool

**Goal:** a stack declares a `maxSlots` ceiling; the "pool" is the emergent set of its
workspaces. No `pool create`, no Pool object.

**Changes**
- `StackDefinition` gains `MaxSlots` (int). Validate at stack-save time: `MaxSlots ×
  ports-per-stack` must fit the configured range — reject a cap the machine can't honour,
  with the shortfall spelled out.
- `InstanceRecord` gains: `Stack?`, `WorkspaceIndex?`, `Claimed` (bool), `Label?`,
  `ClaimedAt?`, `LastUsedAt?`.
- Pool workspaces are auto-named `<stack>-<n>`; the number is the handle.
- `PoolService` reads the live workspace set for a stack by querying `InstanceStore` for
  records tagged with it — no separate `pools/` persistence.
- `sprig pool status <stack>` — workspace grid (name · claimed? · label · last-used ·
  ports), derived, showing `claimed / maxSlots`.
- Freezing: a stack with any live workspace is frozen (extends the existing
  `StackStore.Save` refusal) — workspace integrity depends on a stable recipe. Raising
  `maxSlots` therefore means unfreezing; acceptable, just noted.

**Deliverable:** set a capacity on a stack; inspect its (initially empty) pool.

**Exit criteria:** saving a stack whose `maxSlots × ports` exceeds the range fails with
the arithmetic; `pool status` on a fresh stack shows `0 / maxSlots`.

---

### M3 — Checkout / release lifecycle

**Goal:** the full loop — claim (with a handling choice), work, hand back cheaply.

**Changes**
- `sprig pool release [stack]` — the cheap, safe half:
  - List **claimed** workspaces (name + label); pick one (`--workspace`/`--label`
    non-interactive).
  - Flip it to **unclaimed** and `docker compose down` (**no `--volumes`**) — containers
    and networks are torn down so it stops burning CPU/RAM, but **nothing is removed from
    disk**: worktrees, branches, node_modules, and named volumes stay exactly as they
    were. A later `up` finds the database right where it was.
  - Always safe: un-pushed commits are preserved, so a mistaken release is recovered by
    claiming the same workspace **as-is**.
- `sprig pool checkout <stack>` — where the state decision happens:
  - Choose a workspace: reuse an **unclaimed** one, or (if live count `< maxSlots`) lazily
    allocate a new `<stack>-<n>` + port set; else fail "pool exhausted — release one".
  - When multiple unclaimed workspaces exist, list them with per-workspace **metadata**
    (last label · age · state) so you can pick the one whose leftover state you want (e.g.
    "the one that already has product-X built"). Auto-pick under `--no-interactive`.
  - Because unclaimed ≠ clean, checkout **prompts for a handling mode** — **fresh / as-is /
    refresh** (§3a) — with flags (`--fresh` / `--as-is` / `--refresh repo,…`) for
    non-interactive use.
  - Prompt for a **label**, apply the chosen handling (reuses M1's refresh path), bring
    infra back **`up`** (fresh does `down --volumes` first for a clean DB), mark
    **claimed**, print the worktree path(s) / how to enter (ties into `cd`/`path`).
  - **Atomic claim:** lock per-stack so two concurrent checkouts can't grab the same
    workspace or blow past `maxSlots`.

**Deliverable:** end-to-end `checkout → work → release → checkout` on real repos, with a
same-workspace **as-is** re-claim completing in seconds.

**Exit criteria:** checkout past `maxSlots` (all claimed) is refused; release removes
nothing from disk (verify node_modules survives) and frees containers; an **as-is**
re-claim resumes the prior state; a **fresh** re-claim is at master with deps preserved;
concurrent checkouts never collide or exceed the cap.

---

### 3a. Workspace handling modes (chosen at checkout)

The core nuance: **"reset" (git resync-to-master) and "remove from disk" are different
axes.** The modes vary the git axis; disk artifacts are preserved unless a mode explicitly
says otherwise.

| Mode | Git (tracked files) | node_modules / build output | Docker volumes | Use when |
|---|---|---|---|---|
| **as-is** | untouched — resume your exact branch & working tree | kept | kept | resuming a 99%-ready checkout; recovering a mis-release |
| **fresh** | resync every repo to `origin/<base>` (discards tracked changes; guard on un-pushed commits) | kept (setup reconciles) | **wiped** (`down --volumes`) | starting new work with a clean base but no bandwidth cost |
| **refresh `repo,…`** | resync only the named repos/modules; the rest stay as-is | kept | kept | one repo needs to move to master; the others are fine as they are |

Every mode ends by bringing infra **`up`** (release left it `down`). **fresh** is the only
mode that wipes volumes — clean runtime data / correct migrations — matching the
resource-freeing symmetry: release `down` (keep volumes) → as-is/refresh `up` (data
intact), or fresh `down --volumes` → `up` (clean DB).

**Deep clean** (actually deleting node_modules / build output) is a deliberate, rare,
separate action — a `--deep` flag on refresh, never a default. This is the only path that
spends the bandwidth you're trying to avoid.

**Future:** replace the coarse mode picker with a per-repo **branch/commit selector** so
you can point each repo at exactly the ref you want — fine-grained control over the
starting state.

---

### M4 — Stack as a self-contained block; setup primacy

**Goal:** make the stack the thing that carries the weight, so standing up a pool is
"clone the repos + run the setup," with **no new per-repo config burden**.

**Changes**
- Elevate setup to the authoritative stand-up: it runs on every checkout/refresh, with
  first-class failure surfacing (a failed setup step marks the workspace degraded, not
  silently claimed).
- Let a stack fully specify a repo — membership + setup (+ optional inline env/compose) —
  so a repo with a thin or absent `.sprig.json` can still be pooled. Existing rich
  `.sprig.json` keeps working; the stack can now carry what used to require it.
- **`.sprig.json` stays essentially as-is** — the effort concentrates on the stack, not on
  reshaping repo config (decided §5). No new `dependsOn` / cross-repo dependency array
  (§4a) — it would cost more than it returns.

**Deliverable:** stand up a pool from a stack that names repos + setup, with minimal
per-repo config.

**Exit criteria:** a stack over 2–3 repos with only setup commands checks out a working
environment; a setup failure is visible and the workspace is flagged, not handed over as
if healthy.

---

### M5 — Everything pooled; cleanup

**Goal:** one mental model — you get a workspace by checking one out of a pool.

**Changes**
- Fold the ad-hoc one-off path (`sprig create` / `sprig ws create` single-repo) into the
  pool flow: a lone repo is just a trivial one-repo stack you check out. Deprecate the
  standalone create verbs with a pointer to `pool checkout` (keep as warning aliases for
  one release; remove later).
- No `Stack → Blueprint` rename (decided §5) — this milestone is *not* a vocabulary sweep.
- `docs/config-reference.md` + `CHANGELOG.md` (use the `bump-version` skill).

**Deliverable:** the only documented way to a workspace is `pool checkout`; old create
verbs warn and redirect.

**Exit criteria:** `sprig create …` prints a deprecation pointer and still works for one
release; docs describe a single pooled workflow with no dangling "create a workspace"
alternative. Revisit a first-class one-off form only on user-backed appetite.

---

### Deferred (out of scope) — shared-running dependency resolution

The cross-repo/RMQ problem that kicked this off: today the only way two repos talk is
co-checked-out in one stack sharing a port. A pool of 6 workspaces each spinning up the
*entire* product suite + its own bus is the "bleed."

**A repo-level `dependsOn` array is explicitly rejected** (§4a) — it would violate the
pure-consumer invariant and add cross-repo wiring pain for little gain. If the bleed ever
actually bites in practice, the lever to reach for is a per-port **resolution mode** —
`own` (allocate + run, today's default), `shared` (wire to one long-lived instance, e.g.
the RMQ bus), `contract` (bind to a schema stub, run nothing) — a *wiring* property on the
stack's ports, not a repo dependency. **Not committed**; noted only so the pool model
isn't designed to preclude it. Do nothing here until real usage demands it.

---

## 4. Cross-cutting concerns

**Two axes, not one (the core safety model).** *Git resync* and *disk removal* are
independent:
- **Git axis** — `fresh`/`refresh` hard-resync tracked files to `origin/<base>`. This is
  the only routinely-destructive step, and it touches *tracked* files only. The guard:
  warn + require `--force` before discarding un-pushed commits.
- **Disk axis** — gitignored heavy artifacts (`node_modules`, build output) and real
  `.env` secrets are **never** removed except by an explicit `--deep`. A hard-resync of
  tracked files leaves them untouched by construction, so no `git clean -fdx`.

Because **release removes nothing**, the classic "reset ate my work" failure is designed
out: the only lossy operation is choosing `fresh`/`refresh` on a repo with un-pushed
commits, and even that is guarded and recoverable (claim **as-is** before you refresh).
Volumes are the one genuinely destructive default (only under `fresh`).

**Port budget.** `maxSlots` × ports-per-stack is a real ceiling against the configured
range. Validate at **stack-save** time; surface the arithmetic on failure. Re-check
just-in-time at checkout in case the range setting shrank since.

**Concurrency.** Checkout/release mutate shared per-stack state — take a per-stack lock;
claim is atomic (test with two concurrent checkouts racing for the last workspace).

**Base ref.** New concept (§M1). Decide default: `origin/HEAD`, `origin/main`, or the ref
HEAD pointed at when the workspace was first built. Recommend per-stack default of
`origin/HEAD` with per-repo override.

**Lazy throughout.** Checkout materializes-or-refreshes; release only frees + `down`.
Released workspaces cost nothing but disk, which is the point (preserved artifacts).

**`--json` contract & non-interactive.** Every pool command needs a `--json` shape and a
`--no-interactive` path (label via `--label`, workspace via `--workspace`, handling via
`--fresh`/`--as-is`/`--refresh`), matching the existing CLI conventions (`GlobalSettings`,
`Interactivity.Resolve`).

**GUI parity.** `Sprig.App` mirrors the lifecycle once the CLI is proven — a pool grid
with checkout/release, after M3.

### 4a. Why no `dependsOn`

A repo declaring it depends on another repo breaks sprig's load-bearing invariant: repos
are pure consumers that reference only their own declared inputs, never another repo or a
stack port (`ConfigReferences`/`SprigConfigValidator` actively reject it). A `dependsOn`
array reintroduces exactly that coupling, plus a dependency graph to resolve, order, and
keep from cycling — pain that outweighs the gain. Cross-repo relationships stay where they
already live: the **stack** composes repos and shares ports; the repo stays ignorant. If a
genuine shared-runtime need emerges, address it as stack-level port wiring (Deferred
section), not repo-level dependencies.

---

## 5. Decisions

*All open decisions resolved 2026-08-11:*

- **No explicit `pool create`** — cap lives on the stack as `maxSlots`; workspaces
  allocate lazily (§1a). "Many pools per stack" deferred as an optional `--name` hatch.
- **Release is metadata-only + `docker compose down` (no `--volumes`)** — frees CPU/RAM,
  nothing removed from disk; checkout brings infra back `up` (§M3).
- **State decided at checkout** — fresh / as-is / refresh (§3a).
- **`fresh` wipes docker volumes** (`down --volumes`); as-is/refresh keep them.
- **Interactive checkout shows per-workspace metadata** (label + age + state) so you can
  pick the leftover state you want; auto-pick under `--no-interactive`.
- **Keep the noun "stack"** — no `Blueprint` rename (M5 is not a vocabulary sweep).
- **`.sprig.json` largely unchanged** — effort concentrates on the stack; **no
  `dependsOn`** (§4a).
- **Everything is pooled** — fold the ad-hoc one-off create into `pool checkout`; revisit
  a standalone form only on user-backed appetite (M5).

No open decisions remain blocking implementation.

---

## 6. Suggested build order

```
M1 (probe) ──▶ M2 (stack cap) ──▶ M3 (checkout/release) ──▶ M4 (self-contained stack) ──▶ M5 (everything pooled)
                                                              │
                                          Deferred: shared-running port wiring — only if the bleed actually bites
```

Ship M1 first and stop to evaluate. If recyclable workspaces are clean, the rest is
additive. If they're not, M1's failure mode is the spec for whatever comes next —
including, if it comes to that, the ground-up rebuild.
