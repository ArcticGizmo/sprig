# Sprig — Task Breakdown: M4 (Multi-repo stacks)

**Milestone goal:** a **stack** is 1+ repos wired together. Add the central-store **repo
registry** and **stack definitions**, generalize the workspace lifecycle to N repos with
**per-repo port namespacing** and **cross-repo `provides`**, and export/import stacks.

**Exit criteria (all demonstrable):**
1. `vue + dotnet` run together in **one** workspace, fully isolated, with the vue `.env`
   pointed at the dotnet API's **allocated** port via `${sprig.provides.dotnet-api.baseUrl}`.
2. A **second** concurrent workspace of the same stack has no collisions (ports, containers,
   worktrees, networks).
3. Teardown/reconcile handle all repos; source repos stay pristine.
4. A stack round-trips through export → import.
5. `dotnet test` green.

**Builds on:** M1–M3 (config/substitution/ports/store, worktree+env+compose lifecycle, CLI).

> Data-model reminder (locked in the plan): repo `.sprig.json` lives *in each repo*; stacks +
> registry + workspace state live *machine-local* in the central store, with export as the
> only sharing mechanism.

---

## M4.0 — Repo registry ✅ DONE
- [x] **M4.0.1** `repos.json` model (`name → absolute path`).
- [x] **M4.0.2** `RepoRegistryStore` Add/Remove/Get/List; validates `.sprig.json` presence,
      derives name from config, idempotent same-path add, rejects name-collision.
- [x] **M4.0.3** 6 unit tests over a temp store.

## M4.1 — Stack definitions
- [ ] **M4.1.1** `StackDefinition` model (central `stacks/<name>.json`): `name`, `repos: string[]`
      (registry names), optional `vars: {name → template}` (stack-level computed variables).
- [ ] **M4.1.2** `StackStore`: `Save`, `Get`, `List`, `Remove`; `Export(name, path)` /
      `Import(path)` (copy the JSON out/in) as the sharing escape hatch.
- [ ] **M4.1.3** Validation: every referenced repo exists in the registry; unique stack name.
- [ ] **M4.1.4** Unit tests: save/get/list/remove, export→import round-trip, unknown-repo error.

## M4.2 — Multi-repo scope + cross-repo provides  *(pure logic — the heart of M4)*
- [ ] **M4.2.1** Port **namespacing**: allocate per-repo ports under keys `<repo>.<portName>`
      so two repos declaring the same port name never collide within a workspace.
- [ ] **M4.2.2** Two-phase resolution in a `StackScopeBuilder`:
      - **Phase 1** — for each repo, build a *self scope* (`workspace` + its own local ports) and
        resolve that repo's `provides` templates → concrete values; collect a global map keyed
        `<repo>.<key>`.
      - **Phase 2** — per repo, build the *full scope*: `workspace` + own local ports +
        `provides.<repo>.<key>` (all repos) + stack `vars`.
- [ ] **M4.2.3** Hard-fail on unresolved/cyclic refs (reuse `SubstitutionEngine`); a repo
      consuming a missing `provides` key errors at create.
- [ ] **M4.2.4** Unit tests: same-named ports isolated; vue consumes `dotnet-api.baseUrl`
      resolving to the API's allocated port; stack var referencing a provide; missing provide → error.

## M4.3 — Generalize the workspace lifecycle to N repos
- [ ] **M4.3.1** Introduce a `ResolvedStack` (list of `(repoRoot, SprigRepoConfig)`), built from
      either a **stack name** (registry + stack def) or an ad-hoc single `--repo <path>`.
- [ ] **M4.3.2** `WorkspaceService.Create` over N repos: allocate all namespaced ports → build
      scopes (M4.2) → per repo: worktree + branch + env clobber + compose generation → one
      `InstanceRecord` with N `InstanceRepo`s + the `Stack` name.
- [ ] **M4.3.3** Rollback unwinds **all** repos created so far on any failure.
- [ ] **M4.3.4** Confirm `Remove`/`Reconciler`/`Up`/`Down` already loop over `record.Repos`
      (they do) — add multi-repo tests; per-repo compose uses project name `sprig-<ws>` (shared
      project groups the stack's containers).
- [ ] **M4.3.5** Integration test (two temp git repos, one providing to the other): create →
      both worktrees, both `.env`s, cross-repo value resolved; teardown clears both.

## M4.4 — CLI
- [ ] **M4.4.1** `repo add <path> [--name]` / `repo ls` / `repo rm <name>`.
- [ ] **M4.4.2** `stack create <name> --repos a,b [--var k=tmpl]` / `stack ls` / `stack show <name>`
      / `stack rm <name>` / `stack export <name> <path>` / `stack import <path>`.
- [ ] **M4.4.3** `templates` — list stacks and the repos they include (objective's wording).
- [ ] **M4.4.4** `create <ws> --stack <name>` (keep `--repo <path>` for ad-hoc single repo).
- [ ] **M4.4.5** `ls`/`info` render multiple repos + namespaced ports.

## M4.5 — Tests
- [ ] **M4.5.1** Registry + stack store unit tests (M4.0/M4.1).
- [ ] **M4.5.2** `StackScopeBuilder` unit tests (M4.2) — the core logic.
- [ ] **M4.5.3** Multi-repo create/teardown integration over temp git repos.

## M4.6 — Verification: vue + dotnet in one workspace
- [ ] **M4.6.1** Extend `sprig-example-vue/.sprig.json` to consume the API: env
      `VITE_API_URL=${sprig.provides.dotnet-api.baseUrl}`; ensure `dotnet-api` provides `baseUrl`.
- [ ] **M4.6.2** `repo add` both; `stack create web+api --repos ...`; `create demo --stack web+api`.
- [ ] **M4.6.3** Confirm: both worktrees exist; vue `.env` has `VITE_API_URL` = the dotnet API's
      allocated port; `up` brings the postgres container; ports/containers/networks isolated.
- [ ] **M4.6.4** Second workspace `demo2` — fully non-colliding; both stacks coexist.
- [ ] **M4.6.5** Teardown both; source repos pristine. Note in `docs/m4-verification.md`.

---

## Notes & decisions
- **Port keys** in the store/record are namespaced `<repo>.<port>`; each repo's *own* scope sees
  local names (`ports.<port>`), plus `provides.<repo>.<key>` for every repo.
- **`provides` may reference own ports (+ workspace)**; a provide referencing another repo's
  provide is allowed (engine recursion) but keep the verification to the simple case.
- Shared compose **project name** `sprig-<ws>` groups a stack's containers (one `up` per repo file).
- **New Core:** `Stacks/` (registry, stack def/store, `ResolvedStack`, `StackScopeBuilder`).
- Commit per sub-milestone; local only.