# Sprig — Implementation Plan

A milestone-based, phased plan to build **sprig**: an easy-to-configure worktree +
infrastructure isolation tool retrofittable to any git-managed repo.

This plan is the output of grilling [`objective.md`](./objective.md). Where the objective
was ambiguous or silent, the resolved decision is recorded below and treated as binding.

---

## 0. Framing & guiding principles

- **Clean-room reboot.** All prior artefacts (the installed sprig `0.1.3` binary, any
  existing `.sprig.json` shapes on disk) are treated as **untrusted and ignored**. This
  plan and `objective.md` are the only sources of truth. The example repos
  (`../sprig-example-vue`, `../sprig-example-dotnet`) are used **only as test fixtures**.
- **Functional before polished.** Every milestone is a thin, runnable, end-to-end slice
  that works against the example repos. Visible UI is deliberately *last*.
- **Engine-first.** All risk and logic live in a UI-free `Sprig.Core`. A scrappy internal
  **CLI harness** drives it during development so each milestone is testable without a UI.
  The CLI is a *means to functional*, not a shipped product.
- **Front-load investigation.** The three biggest unknowns are de-risked in **M0 spikes**
  before any real building.

---

## 1. Architecture & stack

Modelled on `../perch` (proven blueprint):

- **.NET 10 · Avalonia · C#**, multi-project solution (`sprig.slnx`).
- `src/Sprig.Core/` — UI-free engine, plain `net10.0`, no Windows-only deps. All logic +
  platform-service **interfaces**. This is the product's spine.
- `src/Sprig.Cli/` — internal harness exe that drives `Sprig.Core`. Scrappy by design.
- `src/Sprig.App/` — the Avalonia desktop head (dark mode), lands in M6. Consumes a proven
  `Sprig.Core`. MVVM via `CommunityToolkit.Mvvm`; headless-render for UI verification.
- `tests/Sprig.Tests/` — xUnit over `Sprig.Core`, plus integration tests against the
  example repos.

**Platform: Windows-first, Core OS-agnostic.** `Sprig.Core` avoids Windows-only APIs; git,
docker, and file IO are cross-platform; paths / `%LOCALAPPDATA%` / shell-out are abstracted
so a macOS/Linux head is *possible later* (à la perch) but is **not built or tested now**.

---

## 2. Data model — three-layer split

Resolves the objective's open question (portability vs. coupling):

1. **Repo config → `.sprig.json`, committed *inside each repo*.** Portable/shareable.
   The **only** file sprig ever adds to a source repo's tracked tree. Declares *only that
   repo's own isolation surface* and *only where* overrides apply:
   - named **ports** the repo needs (allocated per-workspace),
   - **env** overrides: which `.env.*` files, which keys, and their `${sprig...}` templates,
   - **compose** overrides: path-based edits into the repo's compose file,
   - **provides**: derived values this repo publishes for other repos to consume.
2. **Stacks / templates → central sprig home** (`%LOCALAPPDATA%\sprig`), **never in a repo.**
   A stack is a named list of repo references (via a local **repo registry**) plus
   stack-level port/variable wiring. **Exportable to a shareable file** as an escape hatch,
   but the local store is the source of truth. No "hub repo" coupling.
3. **Workspace state + port-allocation store → central sprig home, machine-local.**
   Instances, assigned ports, worktree paths, generated compose files, last-known infra
   status. This store is **the source of truth for what *should* exist** and drives teardown.

**File placement rule (binding):**

| Location | Contents | Lifetime |
|---|---|---|
| **Source repo (tracked)** | `.sprig.json` **only** | permanent, committed |
| **Worktree** (`<repo>--<workspace>`, sibling) | clobbered `.env.*` (targeted files only) | dies with the worktree |
| **Central store** (`…\sprig\instances\<ws>\`) | generated compose file(s), port allocations, worktree paths, infra status, run record | until instance removed |

We never hand-maintain a second compose file, and we never store generated artefacts in the
repo. `.env` files *must* live in the worktree (frameworks auto-load them from the project
root); the generated compose lives centrally and is run via
`-f <central>\compose.yml --project-directory <worktree> -p sprig-<workspace>`.

---

## 3. Resolved mechanics (binding decisions)

### 3.1 Substitution engine
- One namespaced string-template engine: `${sprig.<path>}`, resolved **per workspace**.
- Scopes: `${sprig.workspace}` (instance slug), `${sprig.ports.<name>}` (**named** ports,
  not indexed), `${sprig.provides.<repo>.<key>}` (cross-repo values).
- **Stack-level computed variables allowed** (e.g. `API_URL = https://localhost:${sprig.ports.api}`).
- Variable-to-variable references resolved by dependency order with **cycle detection**.
- **Any unresolved reference / unsatisfied declared input = hard failure** at create time
  (never emit a broken `.env`).

### 3.2 `.env` clobbering
- **Top *and* bottom** injection of a marker-delimited block
  (`# >>> sprig >>>` … `# <<< sprig <<<`) so the sprig value wins regardless of the
  framework's first-wins/last-wins load order.
- **Only the specific `.env.*` files named in `.sprig.json`** are touched. Each is **seeded
  by copying the source repo's current copy** into the worktree, then clobbered. Original
  content between markers is preserved.
- Sprig **never mutates the source repo** — all `.env` writes happen in the worktree.
- Idempotent: re-applying replaces the block cleanly; teardown discards the worktree.

### 3.3 Docker compose overrides
- `.sprig.json` declares **only path-based overrides** (YAML path → template), e.g. suffix
  `container_name` with `${sprig.workspace}`, remap `ports[0]` host side to an allocated port.
- Sprig **generates a full modified compose file** (parse source YAML → apply overrides) and
  stores it **only in the central store** per instance — an audit record of exactly what ran.
- **Per-workspace project name** `sprig-<workspace>` auto-applied for baseline network/volume
  isolation, *on top of* the user's explicit overrides.
- The **same allocated port** feeds both the compose `ports:` host side and the app's `.env`
  connection string — guaranteed consistency.
- `up`/`down` keep volumes; full **remove** wipes them (`down -v`).
- Explicitly-`name:`d volumes are the user's responsibility to override (flagged, not
  auto-rewritten) — deeper volume isolation deferred.

### 3.4 Worktree lifecycle & teardown
- Worktrees are siblings named `<repo>--<workspace>` (deterministic, findable).
- Each gets a fresh branch **`sprig/<workspace>`** off the repo's current `HEAD` (base
  configurable later). Pre-existing branches are never touched.
- **Teardown keeps the sprig branch by default** (may hold real work); only `--force`
  deletes it.
- Teardown/reconcile walks each layer **independently and idempotently**, tolerating any
  piece already gone:
  1. infra — `docker compose -p sprig-<ws> down` (best-effort),
  2. worktree — `git worktree remove`; folder deleted manually → `prune`; folder orphaned
     (git unaware) → `rm`,
  3. branch — delete only if sprig-created *and* `--force`,
  4. ports — release to the store,
  5. record — removed last, so an interrupted teardown is resumable.
- **`reconcile`/`doctor`** (detect + repair record-vs-reality drift) is **in scope early** —
  it is the objective's core safety promise, not polish.

---

## 4. Milestones

Each milestone lists its **goal**, **scope**, and **exit criteria** (the demonstrable
"functional" bar). Milestones after M0 each run against the example repos.

### M0 — De-risking spikes  *(throwaway code)*
**Goal:** kill the three biggest unknowns before committing to a design.
- **S1 — env clobber:** prove top+bottom injection wins in **Vite** (`sprig-example-vue`)
  *and* **DotNetEnv** (`sprig-example-dotnet`).
- **S2 — remote compose:** prove a centrally-stored generated compose run with
  `--project-directory <worktree>` works, including a bind-mount / relative-path case.
- **S3 — worktree drift:** prove git worktree add/remove/prune behaviour and each drift case
  (folder deleted, folder orphaned) on Windows.
- **Exit:** a short findings note per spike; any decision above revised if a spike disproves it.

### M1 — Core spine  *(pure logic, no side effects)*
**Goal:** the engine's brain, fully unit-tested.
- `.sprig.json` model + loader/validator; substitution engine; port-allocation store
  (deterministic per-instance assignment, non-colliding across live instances, reclaim on
  release); central-store layout + read/write.
- **Exit:** unit tests green; given a fixture config + a workspace name, the engine resolves
  all variables and allocates a stable, non-colliding port set — with zero filesystem/docker
  side effects.

### M2 — First functional slice: single repo, no infra
**Goal:** the first "it works" moment.
- Workspace create → `git worktree` + `sprig/<ws>` branch → `.env` seed+clobber → teardown →
  reconcile, driven by the internal CLI.
- **Exit:** `sprig-example-vue` runs isolated on an allocated port from its worktree; teardown
  leaves the source repo pristine; reconcile repairs a manually-deleted worktree folder.

### M3 — Infrastructure
**Goal:** isolated docker infra.
- Compose generation into the central store; `up`/`down`/`reset`; `--project-directory` +
  project-name isolation; port↔env cross-wiring.
- **Exit:** `sprig-example-dotnet` runs fully isolated (own postgres container, non-colliding
  port, matching connection string); `down` keeps the volume, `remove` wipes it.

### M4 — Multi-repo stacks
**Goal:** a stack is 1+ repos wired together.
- Stack definition + repo registry; cross-repo `provides`; port/variable wiring across repos;
  whole-stack create/up/down/teardown/reconcile.
- **Exit:** `vue + dotnet` run together in one workspace, fully isolated, with the frontend
  pointed at the API's allocated port via `provides` — reproducible for a second concurrent
  workspace with no collisions.

### M5 — "Easy to configure" onramp
**Goal:** the objective's ease-of-configuration promise.
- `init`/detect to propose a `.sprig.json` for a repo; repo registry management; template
  authoring + stack export/import file.
- **Exit:** pointing sprig at a fresh clone of an example repo produces a working `.sprig.json`
  with minimal edits; a stack round-trips through export/import.

### M6 — Avalonia UI  *(the real deliverable)*
**Goal:** the intuitive UI from the objective, dark mode, perch conventions.
- Four areas: (1) **repos** list + `.sprig.json` editor/`init` flow, (2) **stacks** builder
  (pick repos, wire ports/provides), (3) **workspaces** create/list, (4) per-workspace
  **detail + lifecycle** (up/down/reset/open/teardown) with live-ish infra status and a
  **drift/reconcile** surface.
- **Exit:** every M1–M5 capability is reachable from the UI; headless-render verification in
  place; no logic lives in the UI layer (all in `Sprig.Core`).

### M7 — Polish & packaging
**Goal:** shippable.
- Velopack packaging; `doctor` UX; docs (README, user guide, config reference); error-message
  and empty-state passes.
- **Exit:** a packaged build installs and runs the full flow on a clean Windows machine.

---

## 5. Testing strategy
- **Unit** (xUnit over `Sprig.Core`): substitution engine, config validation, port allocation,
  compose-YAML transforms, `.env` block injection — pure, fast.
- **Integration** (against `../sprig-example-vue`, `../sprig-example-dotnet`): real worktree
  create/teardown/reconcile; real `docker compose up/down` in M3+ (gated on docker
  availability). Each drift scenario has a test.
- **UI** (M6): headless-render snapshots per perch's pattern.

---

## 6. Non-goals (explicitly out of scope)
- Networked team sync (stack **export/import file** is the only sharing mechanism).
- Secrets management/vault (sprig substitutes values; it does not store secrets).
- Non-docker infrastructure providers.
- Cloud / remote workspaces.
- macOS/Linux heads (Core kept OS-agnostic to keep the door open; not built or tested now).
- UI auto-update beyond Velopack packaging.
