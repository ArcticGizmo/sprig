# Sprig — Task Breakdown: M0 & M1

Concrete, executable tasks for the two immediately-actionable milestones. Later milestones
(M2+) are intentionally **not** decomposed yet — M0 spikes may revise design decisions, and
we avoid over-planning work the spikes could invalidate.

Environment confirmed on this machine: **docker 29.5.3**, **.NET SDK 10.0.302**.
Fixtures: `../sprig-example-vue` (Vite, reads `env.PORT`), `../sprig-example-dotnet`
(DotNetEnv → `Configuration["PORT"]` + `ConnectionStrings:Default`, postgres compose).

---

## M0 — De-risking spikes  *(throwaway code; goal is findings, not production)*

> Each spike ends in a short **findings note** appended to `docs/spike-findings.md`. If a
> spike disproves a decision in `implementation-plan.md`, update the plan before M1.

### S1 — `.env` clobber wins in Vite *and* DotNetEnv ✅ DONE (see `spike-findings.md`)
- [x] **S1.1** Duplicate-key semantics — both **last-wins**, neither errors.
- [x] **S1.2** Vite confirmed via `loadEnv` (the exact `vite.config.ts` code path): `PORT=2222`.
- [x] **S1.3** DotNetEnv confirmed via throwaway console: `PORT=2222`, `DUP=second`.
- [x] **S1.4** Inter-file precedence confirmed: targeted `.env.local` beats sibling `.env` in both.
- [x] **S1.5** Recipe locked: top+bottom block, same value; strip-and-replace fallback noted for
      hypothetical error-on-duplicate frameworks (not needed for v1 targets).
- [x] **S1.6** Findings recorded in `docs/spike-findings.md`.

### S2 — Centrally-stored compose via `--project-directory` ✅ DONE (see `spike-findings.md`)
- [x] **S2.1** Central compose built with hand-applied overrides (name suffix, `ports[0]`→25432).
- [x] **S2.2** Came up with suffixed name + remapped port via `--project-directory`.
- [x] **S2.3** Relative bind mount resolved to the **worktree** and the init script executed
      (marker row `resolved-against-worktree` returned from the DB).
- [x] **S2.4** Teardown by project name (`down` / `down -v`); two instances isolated by
      name/port/network; clean afterward.
- [x] **S2.5** Central-only model **holds** — no fallback needed. `--project-directory
      <worktree>` is mandatory on every compose call.

### S3 — git worktree lifecycle + drift on Windows ✅ DONE (see `spike-findings.md`)
- [x] **S3.1** `worktree add -b sprig/spike` gives clean checkout, **no `.env`** (seeding required).
- [x] **S3.2** Removal refuses without `--force` when untracked files exist; `--force` works;
      branch survives.
- [x] **S3.3** Drift A: folder deleted → `list` flags `prunable` → `prune` reconciles.
- [x] **S3.4** Drift B: admin gone/folder remains → not in `list`; detect via central record +
      disk; plain `rm` safe.
- [x] **S3.5** Gotchas: `remove --force` needed; `.git` is a file; locked files + long paths
      flagged for M2.
- [x] **S3.6** 4-state reconciliation matrix recorded in `docs/spike-findings.md`.

---

## M1 — Core spine  *(pure logic, no side effects; fully unit-tested)*

> Exit criterion: given a fixture `.sprig.json` + a workspace name, the engine resolves all
> variables and allocates a stable, non-colliding port set — with **zero** filesystem/docker
> side effects. `dotnet test` green.

### M1.0 — Solution scaffolding ✅ DONE
- [x] **M1.0.1** `sprig.slnx` + Core/Cli/Tests (`net10.0`, Nullable+ImplicitUsings, refs wired).
- [x] **M1.0.2** `Sprig.Cli` prints `--help`/`--version`.
- [x] **M1.0.3** `dotnet build sprig.slnx` / `dotnet test sprig.slnx` (documented; `.gitignore` added).

### M1.1 — `.sprig.json` config model ✅ DONE
- [x] **M1.1.1** Records `SprigRepoConfig` + `PortDeclaration`/`EnvOverride`/`ComposeConfig`/
      `ComposeOverride`. (Reflection-based STJ for now; source-gen deferred to M7 packaging.)
- [x] **M1.1.2** `SprigConfigLoader` throws `SprigConfigException` for missing file / bad JSON.
- [x] **M1.1.3** `SprigConfigValidator` → `ValidationResult` (schema, name, unique/valid port
      names, env file+keys, compose path/template, unknown top-level keys via `JsonExtensionData`).
- [x] **M1.1.4** 12 unit tests green (valid fixture + each malformed case).

### M1.2 — Substitution engine
- [ ] **M1.2.1** Tokenizer for `${sprig.<path>}` (literal text + refs); tolerant of `$` that
      isn't a sprig ref.
- [ ] **M1.2.2** Scope model: `workspace` (slug), `ports.<name>`, `provides.<repo>.<key>`,
      plus stack-level computed vars.
- [ ] **M1.2.3** Resolver with **dependency ordering** (var→var refs) and **cycle detection**;
      unresolved ref or unsatisfied declared input → **hard error** (typed exception listing
      the offending key).
- [ ] **M1.2.4** Unit tests: named-port resolution, var-to-var (`API_URL` from `ports.api`),
      cross-repo `provides`, cycle → error, missing → error, non-ref `$` passthrough.

### M1.3 — Port-allocation store
- [ ] **M1.3.1** `IPortStore` + file-backed impl under the central store. Allocation from a
      configurable base range; **deterministic per instance** (same workspace → same ports
      across restarts, persisted); **non-colliding across live instances**; **reclaim on
      release**.
- [ ] **M1.3.2** Concurrency: safe against two creates racing (file lock / atomic write).
- [ ] **M1.3.3** Unit tests: two workspaces never overlap; re-request returns the same set;
      release frees the range; exhaustion of range → clear error.

### M1.4 — Central store layout
- [ ] **M1.4.1** `ISprigPaths` abstraction (root = `%LOCALAPPDATA%\sprig`, overridable for
      tests) — keeps Core OS-agnostic. Layout: `instances/<ws>/`, `stacks/`, `repos.json`,
      `ports.json`.
- [ ] **M1.4.2** Instance-record read/write (JSON): repos, worktree paths, assigned ports,
      generated-compose path, last-known status. Source of truth for teardown.
- [ ] **M1.4.3** Unit tests over a temp-dir root: round-trip records; tolerate missing/partial
      store; atomic writes (no corruption on interrupted write).

---

## Working agreement
- Commit locally only (never push / never delete branches) — per project memory.
- Update `docs/implementation-plan.md` if a spike revises a decision.
- Decompose M2 into tasks **after** M1 exits and M0 findings are in.
