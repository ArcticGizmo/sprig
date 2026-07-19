# Sprig — Task Breakdown: M3 (Infrastructure)

**Milestone goal:** isolated docker infra. Generate the per-instance compose file into the
central store, bring it up/down/reset via the S2-proven invocation, and cross-wire the
allocated port into both the compose host-port and the app's `.env` connection string.

**Exit criteria (all demonstrable):**
1. `sprig-example-dotnet` runs fully isolated: its own postgres container (suffixed name), a
   non-colliding host port, and a `.env` connection string that matches that port.
2. A second workspace stands up concurrently with no collision.
3. `down` keeps the volume; `rm` (teardown) wipes it (`down -v`).
4. `dotnet test` green (compose generation unit-tested; docker paths guarded/integration).

**Builds on:** M1 (config/substitution/ports/store), M2 (`WorkspaceService`, `GitService`,
`EnvClobberService`, CLI). **Uses S2 findings:** always `-f <central> --project-directory
<worktree> -p sprig-<workspace>`; `down` keeps volumes, `down -v` wipes.

> Scope guard: **still single repo.** Multi-repo stacks + cross-repo `provides` are M4. M3 adds
> the compose dimension to the existing single-repo lifecycle.

---

## M3.0 — Compose generation ✅ DONE
- [x] **M3.0.1** YamlDotNet 18.1.0 added to `Sprig.Core`.
- [x] **M3.0.2** `ComposeGenerator` walks `Path` segments (maps + numeric seq indices) and sets
      the scalar.
- [x] **M3.0.3** Templates resolved via `SubstitutionEngine` + scope.
- [x] **M3.0.4** `GenerateToFile` writes to the central store; missing path/index → `ComposeException`.
- [x] **M3.0.5** 5 unit tests: name+port override, untouched keys preserved, missing path,
      out-of-range index, GenerateToFile.

## M3.1 — Docker service
- [ ] **M3.1.1** `IDockerService` + `DockerService` over `IProcessRunner`.
- [ ] **M3.1.2** `Up(composeFile, projectDir, projectName)` →
      `docker compose -f <file> --project-directory <dir> -p <name> up -d`.
- [ ] **M3.1.3** `Down(..., removeVolumes)` → `down` / `down -v`; `Ps(...)` → parsed status;
      `Config(...)` → validate the generated file resolves.
- [ ] **M3.1.4** `IsAvailable()` probe (`docker compose version`) so callers degrade gracefully
      when docker is absent.

## M3.2 — Generate on create
- [ ] **M3.2.1** Extend `WorkspaceService.Create`: if `config.Compose` is present, generate the
      compose file after the worktree exists and record `GeneratedComposePath` on the
      `InstanceRepo`. (Does **not** auto-`up` — infra is brought up explicitly.)
- [ ] **M3.2.2** Rollback also removes the generated compose on failure.
- [ ] **M3.2.3** Test: create on a repo with compose yields a valid generated file whose host
      port equals the allocated `postgres` port.

## M3.3 — Infra lifecycle + teardown integration
- [ ] **M3.3.1** `WorkspaceService.Up/Down/Reset(workspace, removeVolumes?)` — resolve compose
      file + project dir (worktree) + project name (`sprig-<ws>`) from the record, call
      `DockerService`; update `LastStatus`.
- [ ] **M3.3.2** `Remove` teardown: **first** `docker compose down -v` for each repo with a
      generated compose (best-effort, tolerant of docker-down/absent), then the M2 worktree steps.
- [ ] **M3.3.3** `Status(workspace)` → live `docker compose ps`.
- [ ] **M3.3.4** Tests (guarded on `IsAvailable`): up→ps shows running→down; teardown wipes.

## M3.4 — CLI
- [ ] **M3.4.1** `up <ws>`, `down <ws> [--volumes]`, `reset <ws>`, `status <ws>`.
- [ ] **M3.4.2** `info`/`ls` show cached infra status; graceful message when docker is absent.

## M3.5 — Tests
- [ ] **M3.5.1** `ComposeGenerator` unit tests (pure YAML) — the core testable piece.
- [ ] **M3.5.2** Docker integration guarded by `IsAvailable()`: `Config` validates a generated
      file; a full up→ps→down→down -v cycle on a temp compose. `log()` a skip note if absent.

## M3.6 — Verification against `sprig-example-dotnet`
- [ ] **M3.6.1** Author `sprig-example-dotnet/.sprig.json`: ports `api`+`postgres`; env `.env`
      (`PORT`, `ConnectionStrings__Default` with `Port=${sprig.ports.postgres}`); compose
      overrides (`container_name` suffix, `ports[0]` → `${sprig.ports.postgres}:5432`).
- [ ] **M3.6.2** `create` + `up`; confirm a postgres container with the suffixed name on the
      allocated host port; `psql` connects on that port; `.env` connection string matches.
- [ ] **M3.6.3** Second workspace: non-colliding container/port/network, both up together.
- [ ] **M3.6.4** `down` keeps the volume (data survives a down/up); `rm` wipes it (`down -v`).
- [ ] **M3.6.5** Verification note in `docs/m3-verification.md`.

---

## Notes
- **New Core folders:** `Compose/`, `Docker/`.
- Docker calls are best-effort in teardown — a missing/stopped daemon must never block worktree
  cleanup.
- Commit per sub-milestone; local only.
