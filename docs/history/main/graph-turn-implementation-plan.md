# The Graph Turn — milestone implementation plan

**Status:** Ready to build · 2026-08-17 · companion to `docs/graph-model-redesign.md` (the design;
read it first). All open questions are resolved (see *Locked* below). Personal tooling: commit
locally only — **no push, no branch delete**. Each milestone ends `dotnet build` + `dotnet test`
green and is independently committable.

## Locked decisions (baked into this plan)

- **Substitution:** flat `${sprig.<capability>.<output>}` + `${sprig.workspace}`; reserved `sprig.`
  root. No `.self`/`.needs` in the path — self/needed/workspace are distinguished by UI color/icon.
- **Match by capability name**; `type` is a hint only.
- **`needs` explicit.** **No type system** — the only real provided type is a port.
- **Modules kept**, enriched with provides/needs; a monorepo is its own **local map**; checkout is
  **whole-repo**.
- **No migration.** Fresh **v1** for repo config and map; existing data fixed by hand.
- **Multiple maps** first-class.
- **Provider visibility: nearest-wins** — a same-repo provider beats a map-wide one. No `internal`
  marker for now (add only if an ambiguity forces it).
- **Git-URL portability: upstream** — a map's git URL is the canonical/upstream source;
  `ResolveDefaultBase` already prefers `upstream`. Prototyped early (M5).
- **Deviation granularity:** `map.wiring`/`map.defaults` keyed by **repo + capability**. Add a module
  dimension only if a real collision appears.
- **Names** (`map`, `provides`, `needs`, `capability`) and `workspace` stay.

## The coexistence strategy (how we stay green through the cut)

Retiring stacks is the one move that could redden the build across Core + CLI + App at once. To avoid
that, the **new map engine is built alongside the live stack engine** and exercised through a new,
initially-experimental `sprig map …` CLI surface. The old stack path stays the user-facing default
through **M1–M6**. **M7 flips the switch and deletes the stack code in one deliberate commit**, once
the new path is proven end-to-end. No milestone before M7 removes a stack type.

Reused unchanged throughout: the pool / detached-workspace / branch-on-claim lifecycle
(`WorkspaceService.Claim`, `PoolService`), the port *store* (`FilePortStore` — only the key changes),
compose generation/scanning, `WorkspaceReconciler`, the central-store layout, `RepoRegistry`.

---

## M1 — Repo schema v1: model, validation, references (Sprig.Core, headless)

**Goal:** the model understands `provides`/`needs` on repos and modules, the flat single-module sugar,
and the new substitution namespace. No runtime behaviour change yet — resolve a hand-written v1 repo
config in isolation and validate it.

**1.1 New + revised records** — `src/Sprig.Core/Config/SprigRepoConfig.cs`
- `Capability` (a `provides` entry): `string Capability`, `string? Type`, `IReadOnlyDictionary<string, OutputSpec> Outputs`.
- `OutputSpec` — a union of *port* and *derived string*. JSON is either an object `{ "port": true, "allowed": "8100-8103" }` or a bare string `"http://localhost:${sprig.x.port}"`. Model: `bool IsPort`, `string? Allowed`, `string? Template`. Add a `JsonConverter<OutputSpec>` (object → port, string → template).
- `Need`: `string Capability`, `string? As` (alias; defaults to the capability name when resolving references).
- `SetupCommand`: `string Run`, `string Cwd = ""` (replaces the bare-string setup list — v1 shape, no migration).
- `ModuleDeclaration` gains `IReadOnlyList<Capability> Provides = []` and `IReadOnlyList<Need> Needs = []`; `Setup` becomes `IReadOnlyList<SetupCommand>`.
- `SprigRepoConfig`: `Schema` default `1`; keep top-level `Provides`/`Needs`/`Env`/`Compose`/`Setup` as the **single-app sugar**, surfaced through a retained `EffectiveModules` (synthesise one implicit module named `app`, path `""`, when top-level fields are present; else return `Modules`). This is the documented single-app form, **not** a legacy shim.
- **Delete** `InputDeclaration`. **Delete** `SprigConfigMigration` (no migration).

**1.2 Loader** — `src/Sprig.Core/Config/SprigConfigLoader.cs`
- `SupportedSchema → 1`. Remove the `SprigConfigMigration.Normalize` call from `Parse` (v1 is authored clean; unknown/older shapes fail validation with a clear message).

**1.3 Validation** — `src/Sprig.Core/Config/SprigConfigValidator.cs` (rewrite, still collect-all-issues)
- Schema `== 1`; non-empty `name`; unknown top-level keys rejected (keep `[JsonExtensionData]`).
- Module names identifier + unique; `path` safe-relative (reuse `IsSafeRelativePath`).
- **Provides:** `capability` identifier (letters/digits/`-`/`_`, **no dots** — dots delimit the
  substitution path); unique among a repo's provides (a duplicate is a local ambiguity — flag it for
  v1); each output name identifier; a port output's `allowed` parses as `PortSetSpec`.
- **Needs:** `capability` identifier; `as` (if present) identifier.
- **Reference check (partial, honest):** every `${sprig.<x>}` must be `workspace`, or
  `<cap>.<out>` where `<cap>` is a capability the module **provides** or **needs** (or a need's `as`
  alias). Output-name correctness is enforced only for **self-provided** and **same-repo-provided**
  capabilities — a cross-repo need's outputs aren't known until map resolution, so those are checked
  at resolve time (M3). Document this split.

**1.4 References** — `src/Sprig.Core/Config/ConfigReferences.cs`
- `Templates(config)` walks `EffectiveModules[*]` over `Env[*].Set.Values`,
  `Compose[*].Overrides[*].Template`, and each `Provides[*].Outputs[*]` string template.
- `UndeclaredReferences` diffs against the per-module allowed set from 1.3.

**1.5 Serialization** — `src/Sprig.App/ConfigJson.cs`
- Register the `OutputSpec` converter; confirm camelCase/indent/omit-null still round-trips the new
  shape (add the converter to the shared `JsonSerializerOptions`).

**Tests** (`tests/Sprig.Tests/Config`): OutputSpec converter (port ↔ string, round-trip); a
single-app config (top-level sugar) and a monorepo config both parse to the right `EffectiveModules`;
validator flags — dup capability, dotted capability name, bad `allowed`, undeclared self-output ref,
reference to an undeclared capability; a cross-repo-need output ref is **not** flagged at this layer.

**Done when:** a hand-written v1 `.sprig.json` (single-app **and** monorepo) parses, validates, and its
references resolve to the expected per-module allowed sets. **Commit:** `Repo schema v1: provides/needs model, validation, references`

---

## M2 — Map: model, store, validation (Sprig.Core, headless)

**Goal:** maps are first-class persisted objects; multiple allowed.

**2.1 Records** — new `src/Sprig.Core/Maps/MapDefinition.cs`
- `MapDefinition`: `int Schema (=1)`, `string Name`, `IReadOnlyList<MapRepo> Repos`,
  `IReadOnlyDictionary<string, IReadOnlyDictionary<string,string>> Wiring` (`[repo][capability] = providerCapability`),
  `IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string,string>>> Defaults`
  (`[repo][capability][output] = literal`), and an optional `int? MaxSlots` (pool ceiling; replaces
  `StackDefinition.MaxSlots`).
- `MapRepo`: `string Name`, `string? Repo` (git URL; null = local-registry-only).

**2.2 Store** — new `src/Sprig.Core/Maps/MapStore.cs` (mirror `StackStore`)
- `maps/<name>.json` via `JsonFile`. `List/Get/Save/Delete/Import/Export`.
- `SprigPaths`: add `MapsDir` / `MapFile(name)`.
- **Validate:** name pattern; each repo either registered (`RepoRegistry`) **or** carries a `Repo`
  URL; `wiring`/`defaults` reference repos in the map; `maxSlots ≥ 1` when present. (Capability-level
  validation waits for M3, where the resolver has every repo's config.)
- Unlike stacks, a map is **not frozen** by live workspaces — selection/wiring is resolved at
  checkout, so editing a map never invalidates an existing workspace.

**Tests** (`tests/Sprig.Tests/Maps`): round-trip incl. git-URL repos, wiring, defaults, maxSlots;
validation rejects an unregistered repo with no URL and a wiring entry for an absent repo; two maps
over the same repos coexist.

**Done when:** maps persist and validate against the registry. **Commit:** `Map model + store (schema v1, multiple maps)`

---

## M3 — Capability resolver: two-level wiring, port allocation, gaps (Sprig.Core, headless)

**Goal:** the engine that replaces `StackWiring` — turn a map + a selected repo set into per-module
value scopes, port requests, and a gap list. This is the heart of the redesign.

**3.1 Resolver** — new `src/Sprig.Core/Maps/CapabilityResolver.cs`
- Input: the `MapDefinition`, the selected `ResolvedRepo`s (path + parsed config), and the machine
  `PortPolicy`. Output `ResolvedWorkspace`:
  `IReadOnlyList<ResolvedModule>` (`Repo`, `Module`, `IVariableSource Scope`, materialisation refs),
  `IReadOnlyDictionary<string,int> Ports` (keyed `<cap>.<out>`), and
  `IReadOnlyList<UnsatisfiedNeed>` (`Repo`, `Module`, `Capability`).
- **Algorithm:**
  1. **Collect provides** across every module of every selected repo. A duplicate capability name
     *across repos* is a map-wide ambiguity resolvable only by `wiring`; a duplicate *within a repo*
     was already flagged in M1.
  2. **Port requests:** every provided `{port:true}` output → a `PortRequest(name=`<cap>.<out>`, allowed)`.
     Allocate for **all** selected providers (a provider needs its port to *run*, regardless of who
     consumes it). `allowed` is read straight off the output — `PortConstraintResolver`'s
     binding-tracing is gone.
  3. **Value space:** for each capability, resolve its derived string outputs over its own ports via
     the recursive `SubstitutionEngine` (cycle-detected). Yields `<cap>.<out> → value`.
  4. **Wire needs (nearest-wins):** for each module's each need, pick a provider by —
     (a) a provider **in the same repo**; else (b) the single map-wide provider; else (c) the
     `wiring[repo][capability]` choice among several; else (d) `defaults[repo][capability]`; else
     (e) an inline literal supplied at checkout; else record an `UnsatisfiedNeed`.
  5. **Per-module scope:** a `DictionaryVariableSource` whose keys are `<cap>.<out>` for the module's
     self-provided outputs **and** each wired need's outputs (aliased to the need's `as` when set),
     plus `workspace`. **Now** validate that every template reference resolves (the cross-repo output
     check M1 deferred).
- **Nearest-wins note:** step 4a makes a monorepo self-contained — web→api→db all resolve locally.
  Only genuinely external needs reach steps b–e.

**3.2 Port store** — `src/Sprig.Core/Ports/FilePortStore.cs` needs **no logic change**; callers pass
`<cap>.<out>` as the port name. (The leases file keys by workspace→name→port; the name string is
opaque to it.)

**Tests** (`tests/Sprig.Tests/Maps`): local-first beats map-wide; bubble-up to a map provider;
ambiguity resolved via `wiring`, unresolved ambiguity errors; `defaults` fills a gap; a true gap is
reported (not thrown); ports allocated for every provider incl. an unconsumed one; `allowed`
honoured; derived-output cycle detected; `as`-alias reference resolves.

**Done when:** a hand-written map + two repos resolve to correct scopes/ports, and a deliberately
partial selection yields the expected `UnsatisfiedNeed` list. **Commit:** `Capability resolver: two-level wiring, port allocation, gap report`

---

## M4 — Materialisation on the resolver (Sprig.Core; parallel to stacks)

**Goal:** a workspace can be created **from a map** — worktrees, env, compose, per-module setup — via
a new path that leaves the stack path untouched.

**4.1 New create path** — `src/Sprig.Core/Workspaces/WorkspaceService.cs`
- Add `CreateFromMap(mapName, workspace, selectedRepos?, inlineLiterals?)` beside the existing
  stack `Create`. It calls `CapabilityResolver`, then reuses the **same** materialisation the stack
  path uses, driven by `ResolvedModule` instead of `ResolvedStack`:
  - env clobber per module under `module.Path` (existing `EnvClobberService`, module-aware already);
  - compose generation per module (`ComposeGenerator`), generated filename keeps the
    `docker-compose.<repo>.<module>.<slug>.sprig.yml` scheme;
  - setup per module in `<worktree>/<module.Cwd or path>` — honour the new `SetupCommand.Cwd`.
- If `UnsatisfiedNeed`s exist and no inline literal covers them → **hard fail with the named gap
  list** (rollback), mirroring today's unbound-input failure.
- Scope plumbing: `SprigScope.ForValues` already builds `workspace` + arbitrary string keys — feed it
  the resolver's `<cap>.<out>` dictionary. No `SubstitutionEngine` change (dotted keys already work).

**4.2 Record** — `src/Sprig.Core/Store/InstanceRecord.cs`
- Add `string? Map` and `IReadOnlyList<string> SelectedRepos` (keep `Stack?` until M7 so old records
  still load). `Ports` keys are now `<cap>.<out>` — no schema change, just meaning.
- Optionally add per-module resolved values (for the M9 trace view); default empty so old records load.

**4.3 Pool** — `src/Sprig.Core/Pools/PoolService.cs`
- Add a map-keyed pool alongside the stack-keyed one: membership derived from `InstanceRecord.Map`,
  ceiling from `MapDefinition.MaxSlots`, workspace naming `<map>-<n>`. Checkout/Release logic
  (detached park, keep/fresh, report-only release) is **unchanged** — only the key/source differs.
  **Pool = per map**, with selection resolved at create/claim (a partial selection just materialises
  fewer modules; the warm identity is still the map). Flag if per-(map,selection) pools are ever
  wanted — out of scope for v1.

**4.4 Experimental CLI** — `src/Sprig.Cli/` add a `sprig map` command group (`create`, `ls`, `info`)
that drives `CreateFromMap`. Marked experimental in help. This is how M4/M5 are verified without
touching the stack surface.

**Tests** (`tests/Sprig.Tests/Workspaces`): `CreateFromMap` for a monorepo (all modules materialise,
env under each path, one compose per module, setup in each cwd via `RecordingProcessRunner`); a gap
fails with rollback; a partial selection materialises the subset; record carries `Map`/`SelectedRepos`;
`PoolService` map checkout reuses a warm workspace.

**Done when:** `sprig map create` stands up a real monorepo workspace end-to-end; stacks still work.
**Commit:** `Create workspaces from a map (parallel to stacks)`

---

## M5 — Git-URL bootstrap-on-checkout (early, per review)

**Goal:** a map referencing a repo by git URL can materialise it on a machine that hasn't registered
it — the portability payoff, prototyped early to surface the fork/upstream edges.

**5.1 Bootstrap** — in `CreateFromMap` (or a `MapBootstrapper` helper)
- For each `MapRepo` with a `Repo` URL not in `RepoRegistry`: clone it (into a configured repos
  root), `RepoRegistry.Add` it, then proceed. The clone requires a committed `.sprig.json`.
- **Upstream semantics (O2):** the map URL is the **canonical/upstream** source. Clone sets `origin`
  = the URL; `ResolveDefaultBase` already prefers an `upstream` remote when present, so a later
  fork workflow (add your fork as `origin`, canonical as `upstream`) resolves bases correctly with no
  further change. Document the fork follow-up; don't automate it in v1.

**Tests** (`tests/Sprig.Tests/Maps`): with a fake `IGitService`, an unregistered git-URL repo is
cloned + registered before resolve; an already-registered repo is untouched; a URL repo missing a
`.sprig.json` fails clearly.

**Done when:** a map with one local + one git-URL repo checks out on a clean registry. **Commit:**
`Bootstrap git-URL map repos on checkout (upstream-oriented)`

---

## M6 — `sprig init` inference for schema 1 (Sprig.Core + CLI)

**Goal:** `sprig init` proposes a repo's `provides`/`needs` (and modules), not just env/compose.

**6.1 Inference** — `src/Sprig.Core/Init/InitInspector.cs`
- Keep the existing env/compose/module detection. **Fold in the heuristics from `StackAutowire` /
  `StackOwnerGuess`** (about to be deleted in M7): a `*_port`/URL-shaped env value → a provided port
  output + a `http://localhost:${sprig.<cap>.port}` derived url; a referenced-but-unprovided value →
  a `need`. Emit schema-1 with one `app` module (single-app) or per-directory modules (monorepo).
- `sprig init --print` serialises the proposal (ConfigJson handles the shape after M1).

**Tests** (`tests/Sprig.Tests/Init`): a detected dev port becomes a provided port + derived url; an
external URL env becomes a need; monorepo dirs → modules with per-module provides/needs.

**Done when:** `sprig init --print` on a sample repo emits a sensible v1 config. **Commit:**
`sprig init: infer provides/needs (schema 1)`

---

## M7 — Retire stacks (the big, deliberate cut)

**Goal:** maps become the only wiring model; delete the stack engine in one commit.

- **Delete:** `StackDefinition`, `StackStore`, `StackMigration`, `StackShares`, `StackSelection`,
  `PortExpressions`, `PortConstraintResolver`, `StackWiring`/`ResolvedStack`, `StackAutowire`,
  `StackOwnerGuess` (heuristics already lifted into `InitInspector` in M6). Decide `RepoGraph`'s fate:
  repurpose as the map-graph projection for M9, or delete and rebuild there.
- **`WorkspaceService`:** remove stack `Create`; rename `CreateFromMap` → `Create`. Remove `Stack`
  from `InstanceRecord` (existing workspaces are torn down + recreated — personal tooling, no
  migration, consistent with the pool-model plan).
- **`PoolService`:** drop the stack-keyed pool; map-keyed only.
- **CLI** (`CliApp`, `Commands/PoolCommands`): remove `sprig stack …`; promote `sprig map …` from
  experimental to primary; `sprig create` takes `--map` (+ `--without`/selection, `--set cap.out=…`
  for inline literals). Pool commands key on the map.
- **App:** remove the stack builder canvas wiring and stack list; the repo editor's `inputs` UI is
  replaced in M8. Keep the app compiling (temporary "maps" placeholder screen if needed).

**Tests:** delete stack-only tests; fix any test referencing removed types; full suite green.

**Done when:** no stack type remains; `sprig create --map` is the path; suite green. **Commit:**
`Retire stacks; maps are the only wiring model`

---

## M8 — App: repo editor — provides/needs + module local-map view (Sprig.App)

**Goal:** author a repo's provides/needs/env/compose/setup, per module, with the monorepo's local
wiring visible.

- **`RepoConfigViewModel` / `RepoEditViewModel`:** replace the `inputs` section with **provides**
  (capability + type hint + outputs, each output a port toggle w/ `allowed` or a template box) and
  **needs** (capability + optional `as`). Modules stay tabbed (reuse the M-era tab strip idiom);
  single-app repos show the flat sugar view.
- **Local-map strip:** per repo, show the derived sibling wiring (web→api→db) and any need that will
  **bubble up** to the outer map — the repo's own mini-map, read-only.
- **Typed `SprigTokenBox`:** autocomplete over the module's capabilities + `workspace`, with the
  **color/icon/tooltip** marking self-provided vs needed vs workspace (this is where L1 lives).
- Save guards carry over (reject env overrides on git-tracked files, etc.), re-pathed per module.

**Tests** (`tests/Sprig.Tests/App`): VM round-trips a monorepo config; adding a need updates the
bubble-up strip; the token box classifies self vs need; single-app flat view round-trips.

**Done when:** a monorepo repo is fully authorable in the app, local wiring shown. **Commit:**
`Repo editor: provides/needs + monorepo local-map view`

---

## M9 — App: map canvas + trace view (Sprig.App)

**Goal:** build and read maps visually; trace any resolved value to its provider.

- Evolve the repo-graph canvas into the **map editor**: nodes = repos (expandable to modules), edges =
  derived provides→needs wiring; surface **ambiguities** (pick a provider → writes `map.wiring`) and
  **gaps** (supply a default → writes `map.defaults`). Selection to preview a checkout slice.
- **Trace view (L9):** click a resolved value → highlight its provider and the hop(s) — the graph is
  the answer to “where did this come from?”, the thing lost when dataflow stopped being one-directional.
- Map management: create/rename/delete, multiple maps, git-URL repo entries.

**Tests:** map-graph projection (nodes/edges/ambiguity/gap flags) unit-tested; visual correctness
logged to `docs/visual-followups.md` per house practice (desktop screenshots are user-driven).

**Done when:** a map is buildable on the canvas and a value traces to its provider. **Commit:**
`Map canvas + value trace view`

---

## M10 — Docs, changelog, README, version

- Rewrite `docs/config-reference.md` for schema-1 repo + the map (retire the stack section).
- Rewrite `docs/user-guide.md` around select-repos→checkout; add a monorepo local-map walkthrough.
- README **body** rewrite to the new identity (the tagline is already applied); refresh the feature
  bullets (drop "one-directional config"/"partial workspaces" framing; add "self-describing repos",
  "maps", "monorepo local wiring").
- `CHANGELOG.md` + run the **`bump-version`** skill.

**Done when:** docs describe only the map model; changelog + version cut. **Commit:** `Docs + release for the map model`

---

## Sequencing & risk

| Phase | Risk | Note |
|---|---|---|
| M1–M3 | **Low** | Pure headless model/engine, fully unit-tested; nothing user-facing changes. |
| M4–M5 | **Low–med** | New path *added* beside stacks; verified via experimental `sprig map`. Stacks untouched. |
| M6 | Low | Additive inference; lifts heuristics before M7 deletes their old home. |
| **M7** | **Highest — irreversible** | The one big cut. Gated behind a proven M4/M5 path. Delete in a single commit; expect to recreate existing workspaces. |
| M8–M9 | Med (UI) | Logic unit-tested; visuals user-driven. Independent of each other once M7 lands. |
| M10 | Low | Docs/release. |

**The reversible bet** is the map's thinness (repos + deviations) — enrich later. **The irreversible
one** is M7. Everything before it is designed so the new engine is proven in anger (M4–M5) before the
old one is removed — so nothing is ever stranded on a red build.
