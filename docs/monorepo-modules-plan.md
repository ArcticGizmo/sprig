# Monorepo support — per-repo `modules` (schema 3)

## Context

Today a `.sprig.json` describes one repo as a single flat surface: `inputs[]`,
`env[]`, `compose[]`, `setup[]`. That fits a single-app repo, but a **monorepo** is
several slices — `apps/web`, `apps/api`, `packages/worker` — each with its *own*
`.env` files, its *own* compose file(s), and its *own* install/setup step, while they
**share** one set of stack-supplied inputs (ports, URLs).

This change introduces **schema 3**, which adds `modules[]`. Each module owns its own
`env` / `compose` / `setup` and an optional `path` (the subdirectory it lives in);
`inputs[]` stay shared at the repo level. The stack side (bindings, ports, shares) is
**unchanged** — a stack still binds a repo's inputs, and inputs are still repo-level, so
no stack code is touched.

The UI grows to match (see the two mock-ups this plan was written against):

- **Preview panel** — inputs at the top (as now), then a **tab strip, one tab per
  module**; each tab summarises that module's `.env`, docker, and setup.
- **Edit panel** — inputs at the top (shared, always visible so you can see what's
  declared and what interpolation still needs declaring), then **module tabs** with a
  **`+`** to add a module and a **delete** to remove one — down to **zero** modules, so a
  repo can be rebuilt from scratch.

### Decisions confirmed with the user

- **Modules-only in schema 3.** `env` / `compose` / `setup` live *only* inside modules.
  A schema-2 file is **migrated on load** into a single default module (lossless), mirroring
  the existing `StackMigration` pattern. No dual top-level/module representation.
- **A module has an optional `path`.** It is the module's working directory — a monorepo
  slice. A module's env/compose **file paths resolve under `path`**, and its **setup runs
  in `<worktree>/<path>`**. Empty `path` = repo root (a normal single-slice repo).
- **`sprig init` module auto-detection is a later milestone.** v1 `init` proposes a single
  default module wrapping what it detects today; auto-splitting a monorepo into N modules
  is a follow-up (Milestone 6).

### Decisions taken by default in this plan (flag if you disagree)

- **The workspace record stays flat.** All of a repo's module compose files are still
  brought up together under one docker-compose project (exactly as multiple compose files
  already are today), so `InstanceRepo` keeps a flat `GeneratedComposePaths`. **Per-module
  `up`/`down` is out of scope for v1** — modules are an authoring + materialisation concept,
  not a runtime lifecycle boundary. This keeps `Up`/`Down`/`Remove`/`Status` untouched.
- **A module has a required, repo-unique `name`** (the tab label). Migration names the
  lone default module `"app"`. Names must be unique within the repo and match the input
  identifier rules (letters/digits/`-`/`_`).
- **Setup outcomes gain an optional `Module` label** (default `null` → old records still
  load) purely so the CLI and Workspaces view can group a failure under its module.

---

## The schema-3 shape

```jsonc
{
  "schema": 3,
  "name": "sprig-example",
  "inputs": [                                  // shared across all modules (unchanged)
    { "name": "web-port", "example": "6010", "description": "from .env VITE_PORT" },
    { "name": "api-port", "example": "5000" }
  ],
  "modules": [
    {
      "name": "web",
      "path": "apps/web",                      // optional; "" = repo root
      "env": [
        { "file": ".env.local", "templates": [".env"],
          "set": { "VITE_PORT": "${sprig.web-port}" } }   // => apps/web/.env.local
      ],
      "compose": [],
      "setup": [ "npm ci" ]                     // runs in <worktree>/apps/web
    },
    {
      "name": "api",
      "path": "apps/api",
      "env": [ { "file": ".env", "set": { "PORT": "${sprig.api-port}" } } ],
      "compose": [ { "file": "docker-compose.yml", "overrides": [
        { "path": ["services","postgres","container_name"], "template": "pg--${sprig.workspace}" }
      ] } ],
      "setup": [ "dotnet restore" ]
    }
  ]
}
```

**Migration (schema 2 → 3), applied in memory on load:**

```jsonc
// before (on disk, schema 2)            // after (in memory, schema 3)
{ "schema": 2, "name": "x",       →      { "schema": 3, "name": "x",
  "inputs": [...],                         "inputs": [...],           // unchanged
  "env": [...], "compose": [...],          "modules": [ { "name": "app", "path": "",
  "setup": [...] }                             "env": [...], "compose": [...], "setup": [...] } ] }
```

`inputs` never move. The file is rewritten to schema 3 the next time it is saved from the
editor (same "upgrade in memory, persist on next save" contract stacks already use).

---

## Milestones

Each milestone is independently buildable and `dotnet test`-green. They are ordered by
dependency: 1 is the foundation; 2 makes the engine honour modules (verifiable from the CLI
with a hand-edited file, no UI); 3 and 4 are the two UI panels; 5 is docs/release.

---

### Milestone 1 — Schema-3 model, migration & validation (Sprig.Core, headless)

**Goal:** the model understands `modules[]`, old files migrate transparently, and validation
is module-aware. No runtime behaviour changes yet — a migrated single-module repo validates
and resolves exactly as today.

**1.1 `SprigRepoConfig` — add modules, retain legacy fields for parsing**
`src/Sprig.Core/Config/SprigRepoConfig.cs`
- New `ModuleDeclaration` record: `Name` (string), `Path` (string, default `""`),
  `Env` (`IReadOnlyList<EnvOverride>`), `Compose` (`IReadOnlyList<ComposeConfig>`),
  `Setup` (`IReadOnlyList<string>`).
- Add `IReadOnlyList<ModuleDeclaration> Modules { get; init; } = []` to `SprigRepoConfig`.
- **Keep** the existing top-level `Env` / `Compose` / `Setup` properties on the record so a
  schema-2 file still *deserialises*. They become "legacy load-only" — the migration lifts
  them into a module and clears them; the validator forbids them once `schema == 3`.
- `Schema` default becomes `3` (via `SupportedSchema`, below).

**1.2 New `SprigConfigMigration` (mirror `StackMigration`)**
New `src/Sprig.Core/Config/SprigConfigMigration.cs`
```csharp
public static class SprigConfigMigration
{
    // Idempotent, forward-tolerant (>= like StackMigration, not the validator's exact !=).
    public static SprigRepoConfig Normalize(SprigRepoConfig c)
    {
        if (c.Schema >= 3) return c;                 // 3+ trusted as-is; validator judges unknowns
        // schema 1/2: wrap the flat surface into one default module, clear the legacy lists.
        var hasFlat = c.Env.Count > 0 || c.Compose.Count > 0 || c.Setup.Count > 0;
        var modules = hasFlat
            ? new[] { new ModuleDeclaration { Name = "app", Path = "",
                        Env = c.Env, Compose = c.Compose, Setup = c.Setup } }
            : Array.Empty<ModuleDeclaration>();
        return c with { Schema = 3, Modules = modules, Env = [], Compose = [], Setup = [] };
    }
}
```

**1.3 Wire migration into the single load choke-point**
`src/Sprig.Core/Config/SprigConfigLoader.cs`
- `SupportedSchema` → `3`.
- In `Parse(...)` (the funnel every load path goes through), return
  `SprigConfigMigration.Normalize(config)` instead of the raw config. This covers all load
  sites at once: `WorkspaceService.LoadValidConfig`, `RepoRegistry.Add`,
  `RepoConfigViewModel.Load`, `RepoEditViewModel.Load`.

**1.4 Validation — module-aware**
`src/Sprig.Core/Config/SprigConfigValidator.cs`
- Keep the schema check exact (`!= SupportedSchema`); by validation time everything is
  normalised to 3, so an on-disk 99 (which `Normalize` leaves untouched) still fails.
- **Reject legacy top-level fields at schema 3:** if any of `config.Env/Compose/Setup` is
  non-empty, add an issue (`env`/`compose`/`setup`: "must live inside a module in schema 3").
  (These only appear if someone hand-writes a malformed schema-3 file; migrated files clear
  them.)
- New `ValidateModules`:
  - `name`: non-empty, identifier chars (reuse `IsIdentifier`), **unique within the repo**.
  - `path`: optional; if set, must be a relative path (no rooted/`..`-escaping segments) —
    add a small `IsSafeRelativePath` helper.
  - Reuse the existing `ValidateEnv` / `ValidateCompose` / `ValidateSetup` bodies **per
    module**, re-pathed to `modules[m].env[i]...`, `modules[m].compose[c]...`,
    `modules[m].setup[i]`. Refactor those three methods to take the list + a path prefix.
  - **Compose-file dedup** (today repo-wide via `seenFiles`) moves to **per-effective-path**
    — key on `(module.path + "/" + compose.file)` so two modules may each have their own
    `docker-compose.yml` at different paths, but a collision on the same effective path is
    still caught (this is what prevents the generated-filename clash in Milestone 2).

**1.5 `ConfigReferences` — walk modules**
`src/Sprig.Core/Config/ConfigReferences.cs`
- `Templates(config)` iterates `config.Modules[*].Env[*].Set.Values` and
  `config.Modules[*].Compose[*].Overrides[*].Template` instead of the flat lists.
- `UndeclaredReferences` still diffs against the repo-level `config.Inputs` + `workspace` —
  **inputs are shared, so this is exactly the behaviour we want** (a module override that
  references an undeclared input surfaces the same way, feeding the edit panel's "variables
  you should add" strip across *all* modules).

**Tests (tests/Sprig.Tests/Config)**
- `SprigConfigMigration`: schema-2 flat → one `app` module (env/compose/setup moved, inputs
  intact, top-level cleared); empty flat → zero modules; schema-3 untouched; idempotent.
- `SprigConfigLoader`: parsing a schema-2 fixture yields a schema-3 in-memory config with one
  module; a schema-3 fixture round-trips modules.
- `SprigConfigValidator`: duplicate module name flagged; bad module name chars flagged;
  unsafe `path` flagged; two modules sharing an effective compose path flagged; two modules
  with same file name but different `path` **allowed**; top-level env/compose/setup at schema 3
  flagged; undeclared ref inside a module flagged at `template`.
- Update existing `SprigConfigTests` fixtures that assert `c.Env` / `c.Compose` directly —
  they now assert via `c.Modules[0].Env` etc. (or keep a schema-2 fixture and assert the
  migrated shape).

**Done when:** `dotnet build && dotnet test` green; a schema-2 `.sprig.json` on disk loads,
migrates, and validates unchanged.

---

### Milestone 2 — Materialisation honours modules & `path` (Sprig.Core engine)

**Goal:** `sprig create` writes each module's env under its `path`, generates a compose file
per module (uniquely named), and runs each module's setup in `<worktree>/<path>`. Fully
verifiable from the CLI with a hand-written schema-3 file — no UI needed.

**2.1 Env clobber under `module.path`**
`src/Sprig.Core/Env/EnvClobberService.cs`
- `Apply` / `Strip` currently take the whole `config` and iterate `config.Env`, writing to
  `worktree/<file>`. Change the loop to iterate **modules**, joining `module.path`:
  target = `Path.Combine(worktree, module.Path, over.File)`; seed templates likewise resolved
  under `module.Path` in the source repo. Simplest: add an overload
  `Apply(IEnumerable<EnvOverride> env, string modulePath, string sourceRepo, string worktree,
  scope)` and have the module loop live in `WorkspaceService` (keeps the service a thin
  per-file writer, consistent with `ComposeGenerator`). `EnvKeyReader` is path-based and
  needs no change.

**2.2 Compose generation — per module, unique filename**
`src/Sprig.Core/Workspaces/WorkspaceService.cs` (`Create`, ~161-219)
- Replace `foreach composeCfg in repo.Config.Compose` with a nested walk:
  `foreach module in repo.Config.Modules → foreach composeCfg in module.Compose`.
- Source compose path resolves under `module.Path`:
  `Path.Combine(repo.Root, module.Path, composeCfg.File)`.
- **Generated filename gains a module segment** to avoid the collision the exploration
  flagged: `docker-compose.{repo.Name}.{module.Name}.{ComposeSlug(composeCfg.File)}.sprig.yml`.
  Still collected into the same flat `composePaths` → `InstanceRepo.GeneratedComposePaths`
  (all modules up together, per the default decision).
- `ComposeGenerator` itself is already per-`ComposeConfig` — no change.

**2.3 Setup per module, in the module's working dir**
`src/Sprig.Core/Workspaces/WorkspaceService.cs` (`HasSetup`, `RunSetup`, `PlanCreate`)
- `RunSetup` runs each module's `setup` with working directory
  `Path.Combine(plan.Worktree, module.Path)` (was always `plan.Worktree`).
- `HasSetup(repo)` = any module has setup. `PlanCreate` checklist granularity: an "Install
  dependencies" row **per module that has setup**, with a sub-row per command
  (extend `CreateStepIds` to key on `(repo, module)` — e.g. `Setup(repo.Name, module.Name)`).
- Tag each `SetupOutcome` with its `Module` (see 2.4).

**2.4 Record: label setup outcomes with their module**
`src/Sprig.Core/Store/InstanceRecord.cs`
- Add optional `string? Module { get; init; }` to `SetupOutcome` (default `null` → old records
  still deserialise). `GeneratedComposePaths` / `Setup` stay flat on `InstanceRepo`.
  `Up`/`Down`/`Remove`/`Status` untouched.

**2.5 `sprig init` emits schema 3 (single default module)**
`src/Sprig.Core/Init/InitInspector.cs`, `src/Sprig.Cli/CliApp.cs`
- `Inspect` wraps its detected `Env`/`Compose` into one `ModuleDeclaration { Name = "app",
  Path = "" }` and emits `Schema = 3`. (Directory-grouped multi-module detection is
  Milestone 6.) The CLI already serialises the whole config object, so it adapts to the new
  shape once `Schema` is 3.

**2.6 CLI create output — group setup by module**
`src/Sprig.Cli/CliApp.cs` (`create`, ~98-113)
- When a repo has more than one module (or any module has a non-empty `path`), print setup
  outcomes grouped under `module (path)` headers using the new `SetupOutcome.Module`. Single
  default module keeps today's flat output.

**Tests (tests/Sprig.Tests/Workspaces, /Env)**
- `WorkspaceService`: a two-module repo generates two uniquely-named compose files;
  env for module `web` is written at `<worktree>/apps/web/.env.local`; setup for `api` runs
  with cwd `<worktree>/apps/api` (assert via `RecordingProcessRunner`); a failing module
  setup still creates the workspace (existing soft-warning contract) and the outcome carries
  the module label; `PlanCreate` emits per-module setup rows.
- `EnvClobberService`: writes under `module.path`; seeds templates resolved under `module.path`.
- `InitInspector`: proposal is schema 3 with one `app` module.

**Done when:** hand-write a two-module `.sprig.json`, `sprig create` a workspace, and confirm
on disk: env files under each module path, two generated compose files in the store, setup ran
in each module dir, record + CLI output group by module.

---

### Milestone 3 — Preview panel: module tabs (Sprig.App, read-only)

**Goal:** the read-only Configuration panel matches mock-up 1 — inputs at the top, then a tab
strip with one tab per module, each summarising its env / docker / setup.

**3.1 `RepoConfigViewModel` — project modules**
`src/Sprig.App/ViewModels/RepoConfigViewModel.cs`
- Keep `Inputs` at the top (unchanged). Replace the flat `Env`/`Compose`/`Setup` collections
  with `IReadOnlyList<ModuleTab> Modules`, where `ModuleTab` is a small record: `Name`, `Path`,
  and the existing `Env` (`EnvGroup`), `Compose` (`ComposeInfo`), `Setup` (strings) projections
  plus `HasEnv/HasCompose/HasSetup`. `Load` builds one `ModuleTab` per `config.Modules`.
  Add `SelectedModule` (default first) + `HasModules`.

**3.2 A reusable tab strip (the app has no `TabControl` today)**
- The app uses no Avalonia `TabControl` anywhere. Rather than adopt Fluent `TabControl`, build
  a lightweight strip consistent with the existing chip/pill idiom (`MissingInputRefs`
  `WrapPanel` at `ReposView.axaml:373-385`): an `ItemsControl` of tab buttons over `Modules`,
  each button `Command`-bound to a `SelectModule` action and style-toggled on
  `SelectedModule`, above a content region bound to `SelectedModule`. Reuse the existing
  brushes (`AccentBrush`/`MutedBrush`/`PanelBgBrush`/`BorderBrush`). Factor it into a small
  templated control or a `UserControl` so Milestone 4 reuses the exact same strip.

**3.3 `ReposView.axaml` preview section**
`src/Sprig.App/Views/ReposView.axaml` (preview `ScrollViewer`, ~72-243)
- Leave the INPUTS block (151-172) exactly where it is.
- Replace the flat ENV/COMPOSE/SETUP `ItemsControl`s (175-240) with the tab strip + a
  per-module content panel that reuses the same ENV/COMPOSE/SETUP sub-templates against
  `SelectedModule`. Show the module `path` as a subtle subheading when non-empty.
- Gate on `HasModules`; show a muted "No modules yet" line when zero.

**Tests (tests/Sprig.Tests/App)**
- `RepoConfigViewModel`: a two-module config yields two `ModuleTab`s with the right env/compose/
  setup summaries and `Has*` flags; `SelectedModule` defaults to the first; a migrated
  schema-2 fixture yields a single `app` tab.

**Done when:** selecting a monorepo repo shows inputs on top and one tab per module, each
summarising env/docker/setup (mock-up 1).

---

### Milestone 4 — Edit panel: module tabs, add & delete (Sprig.App, edit)

**Goal:** the editor matches mock-up 2 — inputs at the top (shared, always visible, with the
live "variables you should add" hint), then module tabs with a **`+`** to add and a **delete**
to remove down to zero.

**4.1 `RepoEditViewModel` — module tabs owning the per-module rows**
`src/Sprig.App/ViewModels/RepoEditViewModel.cs`
- **Inputs stay at repo level, unchanged:** the `Inputs` collection, `SprigVariableNames`,
  and the `MissingInputRefs` quick-add strip all remain top-level. `MissingInputRefs` must be
  recomputed from **every module's** overrides (via `ConfigReferences.UndeclaredReferences(
  Build())`, which already walks modules after Milestone 1) — so a ref used in *any* module
  chips up at the top. This is the whole reason inputs sit above the tabs (dedup + interpolation
  hint across modules).
- New `ModuleEditTab : ObservableObject` owning what the flat editor owns today: `Name`,
  `Path`, `ObservableCollection<EnvFileEditRow> Env`, `ObservableCollection<ComposeFileEditRow>
  Compose`, `ObservableCollection<SetupCommandRow> Setup`, plus the existing per-section add
  commands (`AddEnvFile`/`AddComposeFile`/`AddSetupCommand`) scoped to the tab, and its own
  `HasEnv/HasCompose/HasSetup`. Essentially: lift the current `Env/Compose/Setup` collections
  and their add/remove wiring off the VM and onto the tab.
- `ObservableCollection<ModuleEditTab> Modules`, `SelectedModule`, `AddModuleCommand` (`+` →
  new empty tab, auto-selected, default `Name` like `module-2` ensuring uniqueness),
  `RemoveModuleCommand` (delete a tab — allowed down to zero; if the removed tab was selected,
  select the previous/next or none).
- `Load`: build one `ModuleEditTab` per `config.Modules` (each hydrates its Env rows
  incl. templates, Compose rows, Setup rows exactly as the flat editor does today). The
  per-file `git` tracked/ignored classification and the Env/Compose overlays are **built per
  tab**, joining `module.Path` when resolving files on disk (tracked-set lookup and
  `git.IsIgnored` must see `Path.Combine(module.Path, file)`).
- `Build`: reconstruct `Modules` (each `ModuleDeclaration` with `Name`, `Path`, and the tab's
  Env/Compose/Setup projections). The reconstructed config is schema 3.
- The overlays (`EnvOverlayViewModel`/`ComposeOverlayViewModel`) and `SprigTokenBox`
  `Variables` binding are unchanged — `Variables` still comes from the repo-level
  `SprigVariableNames`, since inputs are shared.

**4.2 `Save` — module-aware checks**
`RepoEditViewModel.Save`
- The existing guards move per module and gain the path join: reject env overrides on
  git-**tracked** files (now `Path.Combine(module.Path, file)`); reject compose targets not
  present on disk (now under `module.Path`). Then `SprigConfigValidator.Validate(Build())`
  (module-aware after Milestone 1) → `ConfigJson.Write` (unchanged; serialises whatever
  `Build()` returns, so schema-3 modules persist automatically).

**4.3 `ReposView.axaml` edit body**
`src/Sprig.App/Views/ReposView.axaml` (edit overlay body `StackPanel`, ~284-538)
- Keep INPUTS (314-388) and the `MissingInputRefs` strip at the top, unchanged.
- Below it, the **same tab strip control from Milestone 3**, but with a trailing **`+` button**
  (`AddModuleCommand`) in the strip and a **delete (`✕`) affordance per tab** (or a "Delete
  module" button in the tab header) bound to `RemoveModuleCommand`. A `path` `TextBox` at the
  top of the tab body. The ENV OVERRIDES / DOCKER COMPOSE OVERRIDES / SETUP COMMANDS sections
  (391-536) move inside the tab body, bound to `SelectedModule`'s collections. Show an empty
  state with a single "**+ Add module**" call to action when `Modules` is empty.

**Tests (tests/Sprig.Tests/App)**
- `RepoEditViewModel`: `Load → Build` round-trips a two-module config (names, paths, env/
  compose/setup per tab); `AddModule` appends a selectable empty tab and `RemoveModule` can go
  to zero; `MissingInputRefs` reflects a reference made in the *second* module; a blank
  setup/env row is dropped on `Build` (existing behaviour, now per tab); Save rejects a
  tracked env file under a module path.

**Done when:** editing a repo shows inputs on top, a tab per module with `+`/delete, editing a
tab's env/docker/setup persists to schema-3 `.sprig.json`, and deleting all modules then saving
yields a `modules: []` config (mock-up 2).

---

### Milestone 5 — Docs, changelog & release

**Goal:** the new surface is documented and versioned.

- `docs/config-reference.md`: rewrite the repo-config section for schema 3 — the `modules[]`
  table (`name`, `path`, `env`, `compose`, `setup`), how `path` scopes file paths and setup
  cwd, the shared-inputs note, and a **Migration** subsection (schema 2 → single `app` module,
  persisted on next save). Update the two worked examples and add a monorepo example.
- `docs/user-guide.md`: a short "Monorepos — one repo, many modules" walkthrough (add a
  module, set its path, per-module env/setup, delete a module).
- `CHANGELOG.md` `[Unreleased]`: Added — modules / monorepo support; Changed — repo editor &
  preview now tabbed per module; schema bumped to 3 with transparent migration.
- Run the **`bump-version`** skill to cut the release entry.
- Keep this plan at `docs/monorepo-modules-plan.md` (done) per the artefact rule.

**Done when:** docs describe schema 3, changelog + version updated.

---

### Milestone 6 — Later / out of scope for v1

- **`sprig init` auto-detects modules** — group `DetectEnv`/`DetectCompose` results by
  directory (both already work per-directory/-file) into one module per slice, with `path`
  set. Propose module names from directory names.
- **Per-module `up`/`down`/status** — give `InstanceRepo` a module dimension so a workspace's
  infra can be controlled per module. Touches `Up`/`Down`/`Remove`/`Status` and the Workspaces
  VMs; deliberately excluded from v1 (record stays flat).
- **`${sprig.*}` in setup commands**, live setup output streaming — pre-existing backlog,
  unchanged by this work.

---

## Verification (end to end)

1. `dotnet build && dotnet test` green after each milestone.
2. **Migration:** open an existing schema-2 repo in the app → it shows one `app` module;
   save → `.sprig.json` is now schema 3 with `modules: [ { "name": "app", ... } ]`,
   `inputs` unchanged.
3. **Authoring:** add a second module `api` with `path: apps/api`, an env override and a
   `dotnet restore` setup; the top-level "variables you should add" strip reflects a
   `${sprig.*}` used only in `api`.
4. **Materialise:** create a workspace → env files land under each module's path, one
   generated compose per module in the store, setup ran in each module's dir; a failing
   module setup is a soft warning naming the module.
5. **Rebuild-from-scratch:** delete every module in the editor, save → `modules: []`; re-add
   modules and confirm round-trip.

## Risk & sequencing notes

- **Milestone 1 is a hard prerequisite** for everything: it defines the model, migration, and
  the module-aware `ConfigReferences`/validator that both the engine (2) and the editor's
  interpolation hint (4) depend on.
- Milestones 3 and 4 are independent of each other and can be built in parallel once 1 is in;
  4 is the larger (it moves the whole per-file editing + overlay + git-safety machinery onto
  tabs).
- The **flat-record decision** is the main reversible bet: if per-module lifecycle is wanted
  later, Milestone 6 adds it without disturbing 1–5 (the record grows a module dimension; the
  authoring/materialisation model is unchanged).
