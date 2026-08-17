# The Graph Turn — from hand-wired stacks to self-describing repos

**Status:** Design proposal (v2, incorporating review) · 2026-08-17 · supersedes the *stack* concept
described in `docs/config-reference.md`, `docs/wiring-ux-plan.md`, and
`docs/graphic-stack-builder-plan.md`. The **pool / detached-workspace / branch-on-claim** lifecycle
(`docs/pool-detached-model-plan.md`) is **unaffected** — this changes the *wiring/config* layer, not
the *lifecycle* layer.

> **Clean break, no migration.** This is a fresh **v1** schema for both repo configs and maps. There
> is no automatic migration from the old `inputs`/stacks/schema-3 shapes — existing data is fixed up
> by hand. The old 1/2/3 lineage is abandoned; the new shapes are unmistakably different.

---

## 1. Where sprig is today (an honest snapshot)

sprig began as *"consistent port resolution for worktrees."* It grew into a full isolation layer —
worktrees, branches, non-colliding ports, docker infra — composed into **stacks** and checked out as
**workspaces** (pooled, detached-until-claim). The whole thing rests on a rule stated plainly in the
README:

> *"A repo never produces values for another repo — the stack wires them together. Every value
> originates in the stack and flows one way into a repo."*

- **`.sprig.json` (schema 3)** — a repo is a **pure consumer**: it declares `inputs[]` and
  `modules[]`, and says nothing about what it offers.
- **Stack (schema 2)** — the sole producer. Owns `ports[]`; binds every repo's every input via
  `bindings[repo][input] = expression`. `shares[]`/`owners[]` are validated *viz-only* overlays.
- **Substitution** — `${sprig.workspace}`, `${sprig.ports.<name>}` (bindings only),
  `${sprig.<input>}` (repo templates).

### Why it stopped fitting

The value you found wasn't port management — it was *"construct an entire set of repos that's ready
to go, isolation as deep as I need."* Against that goal, two structural frictions:

1. **The knowledge lives in the wrong place.** "The api listens on `api_port`; the web app's `apiUrl`
   is `http://localhost:api_port`" is a fact about the api and the web app — yet it's re-encoded in
   every stack that combines them. The repos know their own shape best, and say nothing.
2. **Stacks are closed sets, so they multiply.** Every use case is a separately hand-wired stack. You
   want to select the repos *involved* and have the wiring fall out.

The tell is already in the code: `StackAutowire` *guesses* the wiring by name convention,
`StackOwnerGuess` *guesses* which repo owns a port, and the stack keeps an `owners[]` overlay
recording producer→port. The system is already straining toward a producer/consumer graph — by
guesswork, on a model that denies producers exist.

---

## 2. The thesis: repos describe themselves; maps wire the world

Three moves.

### Move 1 — Repos become bidirectional: **`needs` + `provides`**

Overturns *"a repo never produces values."* Every repo (and every module within it) declares what it
**provides** — its own ports, and the values built from them: a base URL, a connection string — and
what it **needs**. Ports move **from the stack onto the repo that owns them**, making the `owners[]`
guess-overlay obsolete: ownership is now *declared*. A repo becomes self-contained enough to drop into
any map and just work.

### Move 2 — **Maps**, not stacks

Overturns *"a stack is a closed, hand-wired combination."* A **map** is an **open graph you take
slices of** — not a pre-wired combination. Wiring is **derived**: for each selected repo's needs,
sprig finds a selected provider and matches them by capability *name*. The map stores only the
**deviations** (which provider wins when several could; a fallback value for a need whose provider you
didn't select). Auto-wire graduates from a one-shot *proposal* to the permanent *resolution
mechanism* — reliable now, because it runs on declared contracts, not guessed conventions.

**Multiple maps are first-class.** One map replaces *many* stacks, but you keep several maps because
either (a) you work a specific way and want different maps over the *same* repos, or (b) you work on
multiple unrelated projects. Maps live at `maps/<name>.json`; the count difference from stacks isn't
the point — *open-slice vs closed-combination* is.

### Move 3 — **Modules stay** — a monorepo is its own local map

*(Revised from the first draft's "drop modules.")* Editing an SPA + API + mobile monorepo from one
flat view is genuinely too complex — modules earn their place. But they're reframed:

- A **module** is a self-describing unit *inside* a repo: `name`, `path`, its own `provides` /
  `needs` / `env` / `compose` / `setup`.
- A repo with several modules is **its own local map**: sibling modules wire to each other by
  capability name (the web module needs `acme-api`; the api module provides it — resolved *locally*,
  inside the repo). A need no sibling satisfies **bubbles up** to the outer map.
- **This is a fractal of the same model:** the same needs/provides/wiring concept and the same
  resolver run at two levels — modules-within-a-repo and repos-within-a-map.
- **Checkout is still whole-repo.** Modules are an *authoring + local-wiring + editor-view* concept,
  **not** a partial-checkout unit (whole repos have always been the checkout granularity). Dropping
  "partial isolation" meant *not slicing a repo at checkout* — it never meant losing per-slice
  authoring.

### The new identity

> **sprig — grow a fully-wired dev environment from any selection of self-describing repos.**
>
> Tagline: *"Self-describing repos. One map of how they fit. Check out any slice, fully wired."*

The old identity was a **mechanism** (worktree + infra isolation with manual stack wiring). The new
one is a **system**: self-describing repos → a shared topology → a fully-wired slice on demand.

---

## 3. Schema changes (fresh **v1**)

### 3.1 `.sprig.json` — schema 1 (self-describing repo)

**Single-app repo** — top-level `provides`/`needs`/`env`/`compose`/`setup` are sugar for one implicit
module (the editor shows a flat view, no tabs):

```jsonc
{
  "schema": 1,
  "name": "dotnet-api",
  "provides": [
    { "capability": "orders-api", "type": "http",      // type is a HINT only
      "outputs": {
        "port": { "port": true },                       // the only real "type": an allocated port
        "url":  "http://localhost:${sprig.orders-api.port}"   // derived string over that port
      } }
  ],
  "needs": [ { "capability": "orders-db" } ],
  "env": [ { "file": ".env", "set": {
      "PORT":  "${sprig.orders-api.port}",              // my own port
      "DB":    "${sprig.orders-db.connString}"          // from whoever provides orders-db
  } } ],
  "setup": [ { "run": "dotnet restore" } ]              // cwd optional; defaults to repo root
}
```

**Monorepo** — `modules[]`, each self-describing; the editor shows one tab per module:

```jsonc
{
  "schema": 1,
  "name": "acme",
  "modules": [
    { "name": "api", "path": "apps/api",
      "provides": [ { "capability": "acme-api", "type": "http",
        "outputs": { "port": { "port": true }, "url": "http://localhost:${sprig.acme-api.port}" } } ],
      "needs": [ { "capability": "acme-db" } ],          // satisfied LOCALLY by the db module
      "env":   [ { "file": ".env", "set": { "PORT": "${sprig.acme-api.port}",
                                            "DB": "${sprig.acme-db.connString}" } } ],
      "setup": [ { "run": "dotnet restore" } ] },

    { "name": "web", "path": "apps/web",
      "needs": [ { "capability": "acme-api" } ],         // wired to the sibling api module
      "env":   [ { "file": ".env.local", "set": { "VITE_API": "${sprig.acme-api.url}" } } ],
      "setup": [ { "run": "npm ci" } ] },

    { "name": "mobile", "path": "apps/mobile",
      "needs": [ { "capability": "acme-api" } ],
      "env":   [ { "file": ".env", "set": { "EXPO_API": "${sprig.acme-api.url}" } } ],
      "setup": [ { "run": "npm ci" } ] },

    { "name": "db", "path": "infra",
      "provides": [ { "capability": "acme-db", "type": "postgres",
        "outputs": { "port": { "port": true },
          "connString": "Host=localhost;Port=${sprig.acme-db.port};Database=acme;Username=acme;Password=acme" } } ],
      "compose": [ { "file": "docker-compose.yml", "overrides": [
        { "path": ["services","db","ports","0"], "template": "${sprig.acme-db.port}:5432" } ] } ] }
  ]

  // Optional repo-local "wiring"/"defaults" — same shape as the map's, for the rare intra-repo
  // deviation (two sibling modules provide the same capability). Omitted here; local auto-wiring
  // (web→api, api→db) is unambiguous.
}
```

Checked out alone, `acme` is fully wired **internally** — web/mobile→api→db all resolve locally,
nothing bubbles out. If `web` also needed an external `auth` capability, that one need would bubble to
the outer map.

Field-by-field vs. today:

| Today (schema 3) | schema 1 | Change |
|---|---|---|
| `inputs[]` | `needs[]` (per repo/module) | Renamed + re-rooted — a need names a *capability* (a contract). |
| — (stack owned ports) | `provides[]` (per repo/module) | **New.** Ports + derived values live on the repo/module that owns them. |
| `modules[]` (env/compose/setup only) | `modules[]` (+ `provides`/`needs`) | **Kept + enriched.** Each module is a local provider/consumer. |
| stack `ports[]` + `bindings[]` | *(gone)* | Wiring is derived; only deviations stored, on the map. |
| `${sprig.<input>}` | `${sprig.<capability>.<output>}` | Flat, capability-qualified (§3.3). |

`allowedPorts` (the Auth0 pinned-callback case) moves onto the provided port output:
`"port": { "port": true, "allowed": "8100-8103" }`.

### 3.2 The map — schema 1 (multiple allowed)

```jsonc
{
  "schema": 1,
  "name": "orders-work",
  "repos": [
    "acme",                                             // by local registry name
    { "name": "billing", "repo": "git@github.com:me/billing.git" }   // name + git URL → portable
  ],
  "wiring":   { "web": { "http-api": "orders-api" } },  // disambiguate when >1 provider matches
  "defaults": { "web": { "auth": { "url": "https://auth.staging.example.com" } } }  // manual fallback
  // no ports[] · no bindings[] · no shares[]/owners[] — all derived from repos' provides/needs
}
```

Thin by construction: in the happy path (unique capability names, all providers selected) a map is
*just a list of repos*. A repo entry may be a bare **registry name** (resolves locally) or
`{ name, repo: <git-url> }` — the git URL makes a map **portable**: on a fresh machine, checkout can
clone + register it. (See the fork/gitflow caveat in §5.)

### 3.3 Substitution — flat, capability-qualified, typed in the UI

Keep the reserved `${sprig.*}` root (so injected values never collide with a framework's own `${…}`
or a shell `$VAR`, which already pass through untouched). **Drop the `.self`/`.needs` sub-namespaces**
— they add no resolution value once a reference is capability-qualified, and the self-vs-needs
distinction is display metadata sprig computes from the module's own declarations.

| Reference | Resolves to |
|---|---|
| `${sprig.<capability>.<output>}` | any capability's output — self-provided *or* needed (renderer colors which) |
| `${sprig.<capability>.<output>}` *inside a `provides` block* | a sibling output of the same capability (recursive resolution, cycle-detected) |
| `${sprig.workspace}` | the workspace slug (reserved built-in; its own color) |

- **Renderer / autocomplete** carries the "type": a distinct color + icon + tooltip for
  *self-provided* vs *needed* vs *workspace*, computed from the surrounding module's declarations. The
  path stays a plain name.
- **Why this is better than `.self`/`.needs`:** promoting a capability from a `need` to a local
  `provide` (exactly the monorepo case) doesn't churn every template that referenced it — the name is
  stable, only its color changes.
- **Optional sugar** (later): inside a `provides` block, `${self.port}` could stand in for
  `${sprig.<own-capability>.port}`. Not needed for v1; the full form is uniform.

---

## 4. Resolution at checkout

The same algorithm runs at two levels — **within a repo** (modules) and **across the map** (repos).
Given a map **M** and a selected repo set **S**:

1. **Collect provides.** Gather every provided output from every module of every repo in **S**.
   Allocate a real, non-colliding host port for each `{ "port": true }` output (per workspace,
   honouring `allowed`). `FilePortStore` is almost unchanged — ports are just keyed by *capability
   output* instead of *stack port name*.
2. **Build the value space.** Resolve each provider's derived outputs (`url`, `connString`, …) over
   its own ports, in dependency order — the existing recursive `SubstitutionEngine` (cycle-detected)
   already does this.
3. **Wire needs — local first, then the map.** For each module's each `need`: resolve against a
   provider **in the same repo** first (local wiring); else a provider elsewhere in **S**; if several
   match, read `M.wiring`; if none, use `M.defaults`, else an inline literal at checkout, else a
   **hard, named gap**: *"web needs auth — add a provider or supply a value."*
4. **Resolve templates.** Evaluate each module's `env`/`compose`/`setup` against the value space +
   `workspace`.
5. **Materialise.** Worktrees → env → compose → setup, per module (setup in `<worktree>/<cwd>`) —
   whole-repo, every module. The detached-park / branch-on-claim lifecycle is untouched.

**Step 3 is where "tell me what to provide manually" lives.** Partial selections don't fail
mysteriously — they produce a precise gap list.

### What stays exactly as-is

- The **pool / detached-workspace / branch-on-claim** lifecycle (`WorkspaceService.Claim`,
  `PoolService`).
- **Ports** — `PortSetSpec`, `PortPolicy`, `FilePortStore`, deterministic-per-workspace allocation.
  Only the *key* changes (capability output, not stack port).
- **Compose** generation/scanning; docker project isolation (`sprig-<workspace>`).
- **Drift-safety** (`WorkspaceReconciler`); the central-store layout; `InstanceRecord` teardown.
- The **repo-graph canvas** — it becomes the map editor (and the monorepo's local-map editor).
- **Modules** as an authoring/materialisation concept — enriched, not removed.

### What's removed

- `StackDefinition`, `StackStore`, `bindings`, `shares`, `owners`, `StackWiring`.
- `StackAutowire`/`StackOwnerGuess` — their *logic* survives as `sprig init` inference.
- No migration code, no `EffectiveModules` legacy-flat shim — v1 is authored clean.

---

## 5. Decisions — locked vs. still open

Most of the earlier open questions are now settled.

### Locked

| # | Decision | Outcome |
|---|---|---|
| L1 | **Substitution shape** | Flat `${sprig.<capability>.<output>}`; self/needs/workspace distinguished by **UI color + icon + tooltip**, not by the path. Reserved `sprig.` root kept. |
| L2 | **Capability matching** | By **name** (the contract). `type` is only ever a **hint**. |
| L3 | **needs are explicit** | Always. Explicit is better — it powers the manual-gap report and lets a need exist without a template reference. |
| L4 | **Modules** | **Kept.** Each is a local provider/consumer; a monorepo is its own local map; checkout stays whole-repo. |
| L5 | **Migration** | **None.** Fresh v1; existing data fixed up by hand. |
| L6 | **Multiple maps** | **First-class.** Several maps for different working styles or unrelated projects. |
| L7 | **Type system** | **Resisted.** The only real provided type is a **port**; everything else is a derived string. |
| L8 | **Naming** | `map`, `provides`, `needs`, `capability`. `workspace` stays. |
| L9 | **Dataflow reversal** (README's core rule dies) | Accepted; the map canvas gains a **trace view** so "where did this value come from?" is a graph hop, not a guess. |

### Still open

1. **Provider visibility.** Are all `provides` visible everywhere with **nearest-wins** (a local
   sibling beats a map provider), or should a module mark a provide **`internal: true`** to hide it
   from the outer map? *Lean:* nearest-wins by default **+** optional `internal` — explicit only where
   it matters, no ceremony on every internal capability.
2. **Registry-name vs git-URL portability (try early).** A map that bootstraps a repo from its git URL
   clones it as `origin` = that URL — which **clashes with a fork/gitflow strategy** where `origin` is
   your fork and `upstream` is canonical. *Lean:* treat the map's URL as the **canonical/upstream**;
   after clone, let the user set their fork as `origin`. Worth prototyping early to feel the sharp
   edges (per review).
3. **Deviation granularity.** Key `map.wiring`/`defaults` by **repo + capability** (simple) or **repo
   + module + capability** (precise)? *Lean:* repo + capability for v1; add module granularity only if
   a real collision appears.

---

## 6. Suggested sequencing

Each phase is independently buildable and `dotnet test`-green; commit locally only (no push).

1. **Schema-1 repo model + validation (headless).** `provides`/`needs` on repo and module; the flat
   single-module sugar; the flat `${sprig.<capability>.<output>}` engine. Resolve a hand-written
   schema-1 repo (mono + single-app) in isolation — including **local module wiring**.
2. **The map + two-level resolver.** Map schema; the §4 resolver (local-first → map, gap report). Port
   store re-keyed to capability outputs. Fully CLI-verifiable.
3. **Git-URL map entries + bootstrap-on-checkout (early experiment).** `{ name, repo }` entries; clone
   + register on a fresh machine; surface the fork/upstream question (open #2). Pulled forward per
   review to derisk portability.
4. **Retire stacks.** Delete `StackDefinition`/`StackWiring`/`bindings`; port create/pool/CLI onto the
   map resolver. *The big, irreversible cut — once the resolver is proven.*
5. **`sprig init` inference for schema 1.** Reuse the `StackAutowire`/`StackOwnerGuess` heuristics to
   propose a repo/module's provides/needs; reuse `InitInspector`'s env/compose detection.
6. **Map canvas + monorepo local-map view + trace view.** Evolve the repo-graph view into the map
   editor; a per-repo local-wiring view for monorepo modules; the typed autocomplete/renderer (L1);
   the trace view (L9).
7. **Docs, changelog, version bump.** Rewrite `config-reference.md` around schema 1 + maps; new
   identity in the README (tagline already applied); `bump-version`.

The reversible bet is the **map's thinness** — "just repos + deviations" is cheap to enrich later. The
irreversible one is **retiring stacks (phase 4)** — gated behind a proven two-level resolver so
nothing is stranded.
