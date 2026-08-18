# The Graph Turn — follow-up plan (post-M8)

**Status:** Planning · picks up after the map model is functionally complete. Companion to
`docs/graph-turn-implementation-plan.md` (the original milestones) and `docs/graph-model-redesign.md`
(the design). Branch `graph-turn`; commit locally only — **no push, no branch delete**. Each item ends
`dotnet build` + `dotnet test` green.

## Where we are (done + green, 717 tests)

The map model is **built and usable end to end**, additive alongside the still-live stack model:

- **M1–M3** — repo schema (`provides`/`needs`, `OutputSpec`), the map + store, and `CapabilityResolver`
  (two-level nearest-wins wiring, port allocation, gap report). Headless, fully tested.
- **M4–M6** — `WorkspaceService.CreateFromMap`; experimental `sprig map ls|show|import|create`; git-URL
  bootstrap on checkout; `sprig init --map` inference. Plus `cli.md`.
- **M7 (part 1)** — map workspaces are **lifecycle-complete**: create / claim / refresh / up / down / rm
  all work (per-module scopes stored on the record).
- **M8** — the App **shows, uses, and authors** maps and map-model repos: the Maps page
  (browse / New-Edit-Delete map / check out a workspace) and the repo editor's per-module
  **PROVIDES / NEEDS** sections (+ the read-only preview).

**Not done:** the stack engine still exists (the App's repo editor and screens still depend on
`inputs`/stacks), and the map App UI has rough edges. This plan covers finishing both.

---

## Workstream A — new repos start map-native  *(small, high value)*

**Problem:** the App's Add-repo flow runs `InitInspector.Inspect` (stack `inputs`), so a freshly added
repo shows `inputs`, not `provides`/`needs`. The map inference (`InspectMap`) already exists (M6) but is
CLI-only.

- Point the App's add/init path at `InitInspector.InspectMap` — `src/Sprig.App/ViewModels/ReposViewModel.cs`
  (the add/init call site) and any first-run/guide path that scaffolds a config.
- The editor already round-trips provides/needs, so no editor change is needed — just the proposal source.
- **Watch:** existing `ReposViewModel`/first-run tests assert an `inputs`-shaped proposal; update them to the
  provides shape (or gate on a flag during the transition).

**Done when:** adding a repo in the App proposes `provides`/`needs`, and the editor opens on them.

---

## Workstream B — the token box understands capability references  *(medium)*

**Problem:** in env/compose overlays, `${sprig.<need>.<output>}` renders as *unrecognized* (red) because
the validity check does exact matching against `SprigVariableNames`, and a need's outputs live in another
repo. Self-provided refs work; needed ones don't. Cosmetic (Save uses the capability-aware validator), but
confusing.

- Make the token validity **capability-aware**, matching `ConfigReferences.UndeclaredReferences`: a dotted
  ref `head.tail` is valid when `head` is a self-provided capability (check the output) **or** a declared
  need/alias (accept any output). Today's list is flat exact-match — needs a small predicate instead.
- Touch points: `src/Sprig.App/ViewModels/EnvOverlayViewModel.cs`, `ComposeOverlayViewModel.cs`, and the
  `SprigTokenBox` control that colours tokens. Feed them the module's provided outputs + needed capability
  names (per-module, not the repo-wide `SprigVariableNames`).
- This is also the right time to make the variable surface **per-module** (capabilities are per-module,
  unlike the old repo-shared inputs).

**Done when:** a valid `${sprig.db.connString}` need-reference isn't flagged red; an unknown capability
still is.

---

## Workstream C — live variable list as provides are typed  *(small; do with B)*

**Problem:** `SprigVariableNames` refreshes on load + input edits, but **not** as you type a provide's
capability/output (removed in M8 because refreshing it from `OnModuleOverridesChanged` re-entered through
the overlays → stack overflow).

- Re-introduce the refresh **without the cycle**: subscribe to `ProvideEditRow.Capability` /
  `OutputEditRow.Name` `PropertyChanged` and recompute — keep the **equality guard** already in
  `RefreshSprigVariableNames` (only mutate the collection when content actually changed), and do **not**
  route it through the shared `OverridesChanged` event the overlays listen to.
- Best folded into B (both are about keeping the token surface current).

**Done when:** typing a new provide output makes `${sprig.<cap>.<out>}` autocomplete/validate live, with no
recursion (guard the ManagementViewModel test — it's the one that overflowed).

---

## Workstream D — visual pass on the new App surfaces  *(user-driven)*

XAML is compile-checked only; the look needs your eyes. Candidates once you've run it:

- Maps page: list/detail balance, the edit form, empty state.
- Repo editor PROVIDES/NEEDS sections: spacing, the port/derived toggle affordance, output row density.
- Colours: PROVIDES green vs NEEDS accent vs the existing env/compose greys — confirm they read as a set.

**Done when:** you've walked the Repos + Maps pages and signed off (or filed specific tweaks).

---

## Workstream E — M9: map canvas + trace view  *(large, visual)*

Evolve the repo-graph canvas into the map editor and add value tracing. This is the big visual piece — do
it interactively.

- **Canvas:** nodes = repos (expandable to modules), edges = derived provides→needs wiring. Surface
  **ambiguities** (>1 provider → pick one, writes `map.wiring`) and **gaps** (no provider → supply a
  default, writes `map.defaults`). Reuse/replace `src/Sprig.App/Controls/RepoGraphCanvas.cs` and the graph
  view-models (`GraphRowViewModel`, `GraphRefViewModel`, `GraphConverters`).
- **Trace view:** click a resolved value → highlight its provider + hop(s). The graph *is* the answer to
  "where did this come from?" — the thing lost when dataflow stopped being one-directional.
- Logic (graph projection: nodes/edges/ambiguity/gap flags) is unit-testable now; visual correctness is
  logged to `docs/visual-followups.md` per house practice.

**Done when:** a map is buildable on the canvas and a value traces to its provider.

---

## Workstream F — retire stacks (the remaining cut)  *(large; the irreversible step)*

The stack engine is still alive because the App and CLI depend on it. Retire it in green stages, in this
order (each stage builds + tests green):

1. **CLI to maps.** Rework the pool onto maps and remove `sprig stack`.
   - Add a map-keyed pool (mirror `PoolService`: membership from `InstanceRecord.Map`, ceiling from
     `MapDefinition.MaxSlots`, `<map>-<n>` naming; the checkout = `CreateFromMap` + claim, which now works).
     Files: `src/Sprig.Core/Pools/PoolService.cs`, `src/Sprig.Cli/Commands/PoolCommands.cs`,
     `WorkspaceCommands.cs` (create `--map`), `CliApp.cs`, `Infrastructure.cs` (drop stack fields from
     `CliContext`). Delete `src/Sprig.Cli/Commands/StackCommands.cs`.
   - **Done when:** the CLI has no `stack` verb; `pool`/`create` are map-based.

2. **App off the stack engine.** With B/E done, the App has a map editor + canvas to replace the stack UI.
   - Delete `StacksView(Model)`, the stack builder canvas, and the stack-based checkout in
     `WorkspacesViewModel`; repoint checkout at the map pool. Remove `inputs` from the repo editor
     (`RepoEditViewModel`/`RepoConfigViewModel`/`ReposView`). Update `AppServices`, `Navigator`,
     `MainWindowViewModel`, and the coach/guide steps that reference stacks.
   - **Done when:** the App builds with no reference to `StackStore`/`StackDefinition`/`inputs`.

3. **Delete the Core stack engine + flip the schema.** Now nothing depends on it.
   - Delete `src/Sprig.Core/Stacks/*` stack types (`StackDefinition`, `StackStore`, `StackMigration`,
     `StackShares`, `StackSelection`, `PortExpressions`, `PortConstraintResolver`, `StackWiring`,
     `StackAutowire`, `StackOwnerGuess`; keep `RepoRegistry`, `RepoGraph` if the canvas reuses it),
     `Workspaces/ResolvedStack.cs`, the stack `Create` in `WorkspaceService`, and `Stack`/`Inputs` from the
     records/config.
   - Remove `InputDeclaration` + `SprigConfigMigration`; set `SupportedSchema = 1`; convert the test
     fixtures + `Demo/SampleFixtures`/`SampleSetup` from `inputs` to `provides`/`needs`.
   - **Done when:** no stack type or `inputs` remains; schema is a clean v1; suite green (expect to recreate
     any existing stack-era workspaces by hand — no migration, per the design).

---

## Workstream G — docs, changelog, version  *(close-out)*

- Rewrite `docs/config-reference.md` around schema-1 repo + the map (retire the stack section);
  `docs/user-guide.md` around select-repos → checkout + a monorepo local-map walkthrough. README body to the
  new identity (tagline already applied). `CHANGELOG.md` + run the **`bump-version`** skill.

---

## Recommended order & risk

| Order | Item | Size | Risk | Why here |
|---|---|---|---|---|
| 1 | **A** new repos map-native | S | low | makes the loop feel finished; cheap |
| 2 | **B + C** token-box + live vars | M | low | removes the biggest authoring wart |
| 3 | **D** visual pass | — | low | your eyes; cheap tweaks before more UI |
| 4 | **E** map canvas + trace | L | med (visual) | the last big map-UI piece; needed before F2 |
| 5 | **F1** CLI to maps | M | low | independent of the App; real retirement |
| 6 | **F2–F3** App off stacks + delete engine | L | **high, irreversible** | gated behind A–E so the App has a full map UI first; the one-way cut |
| 7 | **G** docs/version | S | low | close-out |

**The irreversible step is F3** (deleting the stack engine + flipping the schema). Everything before it is
additive and reversible; F is sequenced so the App has a complete map UI (A–E) and the CLI is already
map-native (F1) before stacks are removed — nothing is stranded on a red build.
