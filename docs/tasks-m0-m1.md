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

### S2 — Centrally-stored compose via `--project-directory`
- [ ] **S2.1** Create a throwaway worktree of the dotnet repo; copy its `docker-compose.yml`
      to a temp "central" dir as `docker-compose.sprig.yml`; apply the objective's overrides
      by hand (`container_name` suffix, `ports[0]` → a free host port).
- [ ] **S2.2** Run `docker compose -f <central>/docker-compose.sprig.yml --project-directory
      <worktree> -p sprig-spike up -d`; confirm container comes up with the suffixed name and
      remapped port; `docker compose -p sprig-spike ps`.
- [ ] **S2.3** **Relative-path stress test** (the flagged wrinkle): add a bind mount
      (e.g. `./init.sql:/docker-entrypoint-initdb.d/init.sql`) to the source compose,
      regenerate centrally, and confirm the relative path resolves against the **worktree**
      (via `--project-directory`), not the central file's dir.
- [ ] **S2.4** Confirm teardown: `docker compose -p sprig-spike down` (keeps volume) and
      `down -v` (wipes). Confirm project-name scoping isolates network/volumes from a second
      `sprig-spike2`.
- [ ] **S2.5** Findings note: does the central-only compose model hold, or is a
      per-worktree/rewritten-paths fallback needed? (This is the design's biggest risk.)

### S3 — git worktree lifecycle + drift on Windows
- [ ] **S3.1** `git worktree add ../<repo>--spike -b sprig/spike` off `HEAD`; confirm clean
      checkout, branch created, untracked `.env` **absent** (validates the seeding requirement).
- [ ] **S3.2** Happy-path removal: `git worktree remove` + branch delete; confirm clean.
- [ ] **S3.3** Drift A — **folder deleted manually**: `rm -rf` the worktree dir, then
      `git worktree list` (stale entry) → `git worktree prune`; confirm reconciliation.
- [ ] **S3.4** Drift B — **orphan folder** (git unaware, like the `--my-third-workspace` dir
      found on disk): a dir that looks like a worktree but isn't in `git worktree list`;
      confirm detection (list vs. disk) and that a plain `rm` is safe.
- [ ] **S3.5** Windows gotchas: locked files while `npm`/docker hold handles; long paths;
      `.git` file vs dir. Record any that affect removal.
- [ ] **S3.6** Findings note: the exact command sequence for each drift case → feeds the M2
      reconcile implementation.

---

## M1 — Core spine  *(pure logic, no side effects; fully unit-tested)*

> Exit criterion: given a fixture `.sprig.json` + a workspace name, the engine resolves all
> variables and allocates a stable, non-colliding port set — with **zero** filesystem/docker
> side effects. `dotnet test` green.

### M1.0 — Solution scaffolding
- [ ] **M1.0.1** `sprig.slnx` with: `src/Sprig.Core/Sprig.Core.csproj` (`net10.0`,
      `Nullable`+`ImplicitUsings` on, no Windows-only deps), `src/Sprig.Cli/Sprig.Cli.csproj`
      (`net10.0`, references Core), `tests/Sprig.Tests/Sprig.Tests.csproj` (xUnit, references
      Core). Mirror perch conventions.
- [ ] **M1.0.2** `Sprig.Cli` prints `--help` and version; wired to build. (Harness only.)
- [ ] **M1.0.3** CI-free local build/test scripts (`build.ps1` / `test.ps1`) or documented
      `dotnet build sprig.slnx` / `dotnet test`.

### M1.1 — `.sprig.json` config model
- [ ] **M1.1.1** C# records for the repo config: `SprigConfig { int Schema; string Name;
      PortDecl[] Ports; EnvOverride[] Env; ComposeOverride Compose; Dictionary Provides; }`
      (shapes per `implementation-plan.md` §2). Use `System.Text.Json` source-gen.
- [ ] **M1.1.2** Loader with clear errors (missing file, bad JSON, unknown schema version).
- [ ] **M1.1.3** Validator: unique port names, non-empty `env.file`/`set`, compose paths
      well-formed, no unknown top-level keys. Returns a structured `ValidationResult`.
- [ ] **M1.1.4** Unit tests: valid fixture parses; each malformed case yields a specific error.

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
