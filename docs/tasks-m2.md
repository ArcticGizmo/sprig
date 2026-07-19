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

## M2.0 — Process runner  *(foundation for git now, docker in M3)*
- [ ] **M2.0.1** `IProcessRunner` in `Sprig.Core/Processes/`: `ProcessResult Run(string exe,
      IReadOnlyList<string> args, string? workingDir = null, CancellationToken ct = default)`
      capturing stdout, stderr, exit code. Arg-array (no shell string) to avoid quoting bugs.
- [ ] **M2.0.2** `ProcessRunner` implementation (`System.Diagnostics.Process`, UTF-8, no window).
- [ ] **M2.0.3** `ProcessResult` helper: `EnsureSuccess()` throws `ProcessException` including
      exe, args, exit code, and captured stderr (so failures are legible).
- [ ] **M2.0.4** Unit tests using a trivial cross-platform exe (e.g. `dotnet --version`, or
      `git --version`) to confirm capture + non-zero handling.

## M2.1 — Git worktree service
- [ ] **M2.1.1** `IGitService` + `GitService` (over `IProcessRunner`) in `Sprig.Core/Git/`.
- [ ] **M2.1.2** `AddWorktree(repo, path, branch)` → `git -C <repo> worktree add <path> -b <branch>`
      (off current HEAD). Fail clearly if branch/path exists.
- [ ] **M2.1.3** `ListWorktrees(repo)` → parse `worktree list --porcelain`, including the
      **`prunable`** flag → `WorktreeInfo(Path, Head, Branch, IsPrunable)`.
- [ ] **M2.1.4** `RemoveWorktree(repo, path)` → always `worktree remove --force <path>`
      (S3: sprig worktrees always carry an untracked `.env`).
- [ ] **M2.1.5** `Prune(repo)` → `worktree prune`; `DeleteBranch(repo, branch)` → `branch -D`
      (called only on forced teardown).
- [ ] **M2.1.6** `IsGitRepo(path)` / `ResolveRepoRoot(path)` guard for `create`.
- [ ] **M2.1.7** Unit tests over a **temp git repo fixture** (helper that `git init`s, commits a
      seed file): add → list shows it → remove --force → gone; delete-folder → list `prunable`
      → prune clears.

## M2.2 — Env clobber writer
- [ ] **M2.2.1** `EnvClobberService` in `Sprig.Core/Env/`. For each `EnvOverride` in config:
      resolve each `Set` value via `SubstitutionEngine` + the workspace scope.
- [ ] **M2.2.2** **Seed**: if the targeted file exists in the **source repo**, copy it into the
      worktree first; else start empty. (S1: only the targeted files; never touch the source.)
- [ ] **M2.2.3** **Clobber**: write the resolved keys in a marker block **at the top and the
      bottom** (`# >>> sprig >>>` … `# <<< sprig <<<`), identical values both ends, original
      seeded content preserved between. **Idempotent** (replace existing sprig blocks).
- [ ] **M2.2.4** `Strip(file)` removes sprig blocks (for reconcile/manual cleanup).
- [ ] **M2.2.5** Unit tests (temp dirs): seed-from-source, top+bottom present, re-apply replaces
      (no duplication), strip restores, non-targeted files untouched, source repo never written.

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
