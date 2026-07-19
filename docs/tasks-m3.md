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

## M3.1 — Docker service ✅ DONE
- [x] **M3.1.1** `IDockerService` + `DockerService` over `IProcessRunner`.
- [x] **M3.1.2** `Up` → `compose -f … --project-directory … -p … up -d` (S2 prefix on every call).
- [x] **M3.1.3** `Down(removeVolumes)` → `down` / `down -v`; `Ps` parses NDJSON *and* array forms.
      (Dropped a separate `Config` method — `Up`/`Ps` failures already surface via `ProcessException`.)
- [x] **M3.1.4** `IsAvailable()` probe.
- [x] Tests: exact arg arrays via `RecordingProcessRunner`, non-zero→throw, ps parse both forms,
      real `IsAvailable` (89 tests).

## M3.2 — Generate on create ✅ DONE
- [x] **M3.2.1** `Create` generates the compose into the central store when `config.Compose`
      present; records `GeneratedComposePath`. No auto-`up`.
- [x] **M3.2.2** Rollback removes the instance dir (incl. generated compose).
- [x] **M3.2.3** Test: generated file lives in the central store; `ports[0]` == allocated
      `postgres` port.

## M3.3 — Infra lifecycle + teardown integration ✅ DONE
- [x] **M3.3.1** `Up/Down(removeVolumes)/Reset` resolve compose+worktree+`sprig-<ws>` and update
      `LastStatus`.
- [x] **M3.3.2** `Remove` brings infra `down -v` first (best-effort, guarded by `IsAvailable`).
- [x] **M3.3.3** `Status(workspace)` → `docker compose ps`.
- [x] **M3.3.4** Fake-docker tests: up/down/reset call docker with `sprig-<ws>`; teardown
      down-with-volumes; require-docker/require-infra/unknown-workspace throw.

## M3.4 — CLI ✅ DONE
- [x] **M3.4.1** `up` / `down [--volumes]` / `reset` / `status` wired + help.
- [x] **M3.4.2** `ls`/`info` show cached `LastStatus`; infra cmds surface a clear error when
      docker is absent.

## M3.5 — Tests ✅ DONE
- [x] **M3.5.1** `ComposeGenerator` unit tests (M3.0).
- [x] **M3.5.2** Orchestration covered deterministically via `FakeDockerService`; real
      end-to-end docker exercised in M3.6 (docker present here). (No hard `IsAvailable` skip
      needed — daemon is available.)

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
