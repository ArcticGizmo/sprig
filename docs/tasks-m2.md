# Sprig — Task Breakdown: M2 (First functional slice)

**Milestone goal:** the first "it works" moment — a single repo, no docker. Create a workspace
→ git worktree + `sprig/<ws>` branch → seed+clobber `.env` → teardown → reconcile, all driven
by the `Sprig.Cli` harness.

**Exit criteria (all demonstrable):**
1. `sprig-example-vue` runs isolated on an allocated port, from its worktree, end to end.
2. Teardown leaves the **source repo pristine** (only `.sprig.json` ever added there).
3. `reconcile` repairs a **manually-deleted worktree folder** (Drift A) and an **orphan
   folder** (Drift B), per the S3 matrix.
4. `dotnet test` green (unit + a hermetic integration test on a temp git repo).

**Builds on M1:** `SprigConfigLoader/Validator`, `SubstitutionEngine`, `SprigScope`,
`FilePortStore`, `InstanceStore`, `ISprigPaths`.
**Uses M0 findings:** env top+bottom recipe (S1); `worktree remove --force` + porcelain
`prunable` + 4-state matrix (S3).

> Scope guard: **single repo only.** Stacks, multi-repo wiring, `provides` consumption, and
> docker infra are explicitly deferred (M3/M4). M2 `create` takes one repo path directly.

---

## M2.0 — Process runner  ✅ DONE
- [x] **M2.0.1** `IProcessRunner.Run(exe, args[], workingDir?, ct)` capturing stdout/stderr/exit.
- [x] **M2.0.2** `ProcessRunner` (System.Diagnostics.Process, UTF-8, no window, async-read,
      cancellation kills the tree).
- [x] **M2.0.3** `ProcessResult.EnsureSuccess()` → `ProcessException` with cmdline + stderr.
- [x] **M2.0.4** 4 tests: stdout capture, non-zero+throw, missing exe, working-dir honoured.

## M2.1 — Git worktree service ✅ DONE
- [x] **M2.1.1** `IGitService` + `GitService` over `IProcessRunner`.
- [x] **M2.1.2** `AddWorktree` (`-b` off HEAD).
- [x] **M2.1.3** `ListWorktrees` porcelain parse → `WorktreeInfo` (incl. `prunable`/bare/detached);
      parser unit-tested directly (via `InternalsVisibleTo`).
- [x] **M2.1.4** `RemoveWorktree` always `--force`.
- [x] **M2.1.5** `Prune` + `DeleteBranch`.
- [x] **M2.1.6** `IsGitRepo` / `ResolveRepoRoot` / `BranchExists`.
- [x] **M2.1.7** 7 tests over `TempGitRepo` fixture: round-trip, force-remove-with-untracked,
      Drift-A prunable→prune, branch survives removal, delete-branch, porcelain parsing.

## M2.2 — Env clobber writer ✅ DONE
- [x] **M2.2.1** `EnvClobberService.Apply` resolves each `Set` value via `SubstitutionEngine`.
- [x] **M2.2.2** Seeds from the source file if present, else writes blocks only.
- [x] **M2.2.3** Top+bottom identical marker blocks, seeded content between; idempotent
      (re-seeds from source + strips any blocks, so no growth).
- [x] **M2.2.4** `Strip` removes sprig blocks (list-and-join, faithful newlines).
- [x] **M2.2.5** 6 tests: seed+wrap, source untouched, absent-source, idempotent re-apply,
      strip restores, only-targeted-files.

## M2.3 — Workspace create (single repo)
- [ ] **M2.3.1** `WorkspaceService.Create(repoPath, workspace, ...)` orchestration:
      load+validate `.sprig.json` → allocate ports (`FilePortStore`) → build `SprigScope` →
      `AddWorktree` at sibling `<repo>--<workspace>` on branch `sprig/<workspace>` →
      `EnvClobberService` → persist `InstanceRecord`.
- [ ] **M2.3.2** Validation gate: refuse if config invalid, workspace name unsafe (path/branch
      chars), repo not git, worktree path already exists, or workspace record already exists.
- [ ] **M2.3.3** **Fail-safe rollback**: if any step throws mid-create, undo what was done
      (remove worktree, release ports, delete partial record) so a failed create leaves no mess.
- [ ] **M2.3.4** Unit/integration test (temp git fixture): create yields a worktree with a
      clobbered `.env`, an instance record, and a port lease.

## M2.4 — Workspace teardown
- [ ] **M2.4.1** `WorkspaceService.Remove(workspace, force)` — layered & idempotent per the S3
      matrix: (1) [infra: none in M2] (2) `RemoveWorktree --force` / `prune` / `rm` orphan as
      the state dictates (3) delete branch **only if `force`** (4) `FilePortStore.Release`
      (5) `InstanceStore.Delete` last (resumable).
- [ ] **M2.4.2** Tolerate every piece already gone (folder missing, branch missing, record
      missing) without throwing.
- [ ] **M2.4.3** Windows lock tolerance: retry `rm` of the worktree folder on `IOException`
      (S3 locked-files note), after worktree removal.
- [ ] **M2.4.4** Tests: happy teardown; teardown with folder already deleted; teardown twice
      (idempotent); `force` deletes branch, non-force keeps it.

## M2.5 — Reconcile / doctor
- [ ] **M2.5.1** `WorkspaceReconciler.Inspect(workspace)` → `ReconcileReport` classifying each
      repo into the 4 states (Healthy / Drift A prunable / Drift B orphan / Gone) by comparing
      the `InstanceRecord` against `ListWorktrees` + disk.
- [ ] **M2.5.2** `Repair(report, ...)` applies the matrix action per state; also detects
      **record-vs-store drift** (leased ports with no record, records with no worktree).
- [ ] **M2.5.3** `InspectAll()` for a whole-store sweep (`doctor`).
- [ ] **M2.5.4** Tests with a **fake `IGitService`** to drive each state deterministically, plus
      one integration test that deletes a real worktree folder and confirms repair.

## M2.6 — CLI wiring (`Sprig.Cli`)
- [ ] **M2.6.1** `create <workspace> --repo <path>` — drives `WorkspaceService.Create`, prints
      worktree path + allocated ports.
- [ ] **M2.6.2** `ls` — table of workspaces from `InstanceStore` (name, repo, ports, status).
- [ ] **M2.6.3** `info <workspace>` — repos, worktree path, ports, reconcile state.
- [ ] **M2.6.4** `rm <workspace> [--force] [--yes]` — teardown; `--yes` required to proceed.
- [ ] **M2.6.5** `reconcile [<workspace>] [--repair]` / `doctor` — show drift, optionally repair.
- [ ] **M2.6.6** Consistent exit codes + `--json` output option (harness-friendly, mirrors plan).

## M2.7 — Verification against the real example
- [ ] **M2.7.1** Author a schema-v1 `.sprig.json` for `sprig-example-vue` (port `frontend`, env
      `.env`/`.env.local` → `PORT`). (This is the one file sprig adds to the repo.)
- [ ] **M2.7.2** `sprig create feat-a --repo ../sprig-example-vue`; `npm run dev` in the
      worktree; confirm it binds the **allocated** port (not 6010).
- [ ] **M2.7.3** Create a **second** workspace; confirm non-colliding port; both dev servers run.
- [ ] **M2.7.4** Delete a worktree folder by hand → `sprig reconcile --repair` fixes it;
      `sprig rm` leaves the source repo pristine (`git status` clean, only `.sprig.json`).
- [ ] **M2.7.5** Record a short verification note in `docs/spike-findings.md` (or a new
      `docs/m2-verification.md`).

---

## Notes & decisions carried in
- **Hermetic tests:** prefer a temp `git init` fixture over touching the user's example repos;
  reserve the real `sprig-example-vue` for the manual M2.7 walkthrough.
- **New Core folders:** `Processes/`, `Git/`, `Env/`, `Workspaces/`.
- **No docker, no stacks, no `provides` consumption** — those are M3/M4.
- Commit per sub-milestone; local only (never push / never delete branches).
