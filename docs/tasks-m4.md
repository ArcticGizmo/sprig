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

## M4.1 — Stack definitions ✅ DONE
- [x] **M4.1.1** `StackDefinition` (`name`, `repos[]`, `vars`).
- [x] **M4.1.2** `StackStore` Save/Get/List/Remove + Export/Import.
- [x] **M4.1.3** Validates repo names against the registry; name pattern allows `+` (e.g. `web+api`).
- [x] **M4.1.4** 5 unit tests incl. export→import round-trip and unknown-repo/import validation.

## M4.2 — Multi-repo scope + cross-repo provides ✅ DONE
- [x] **M4.2.1** Per-repo port maps (namespacing happens at the store layer in M4.3; the builder
      takes each repo's local map so same-named ports are isolated).
- [x] **M4.2.2** `StackScopeBuilder.Build` two-phase (resolve provides against own ports → global
      map; then full per-repo scope with all provides + stack vars).
- [x] **M4.2.3** Unresolved/cyclic refs hard-fail via `SubstitutionEngine`.
- [x] **M4.2.4** 5 unit tests: isolated same-named ports, cross-repo provide consumption, stack
      var → provide, missing provide → error, workspace slug everywhere.

## M4.3 — Generalize the workspace lifecycle to N repos ✅ DONE
- [x] **M4.3.1** `ResolvedStack`/`ResolvedRepo`; `Create(ResolvedStack, ws)` + single-repo
      overload via `ResolveSingleRepo`.
- [x] **M4.3.2** N-repo create: allocate namespaced `<repo>.<port>` → `StackScopeBuilder` → per
      repo worktree+branch+env+compose → one record (N `InstanceRepo`s, per-repo local `Ports`,
      `Stack` name).
- [x] **M4.3.3** Rollback unwinds every worktree/branch created so far + releases ports.
- [x] **M4.3.4** `Remove`/`Reconciler`/`Up`/`Down` already loop over `record.Repos`; per-repo
      compose file `docker-compose.<repo>.sprig.yml`, shared project `sprig-<ws>`.
- [x] **M4.3.5** Integration test: two temp repos, web consumes `api.baseUrl` → correct port in
      `.env`; namespaced non-colliding ports; teardown clears both; second workspace no collision.

## M4.4 — CLI ✅ DONE
- [x] **M4.4.1** `repo add [--name]` / `repo ls` / `repo rm`.
- [x] **M4.4.2** `stack create --repos --var` / `ls` / `show` / `rm` / `export` / `import`.
- [x] **M4.4.3** `templates` lists stacks + repos.
- [x] **M4.4.4** `create --stack <name>` (via `StackResolver`); `--repo <path>` preserved.
- [x] **M4.4.5** `create` prints per-repo worktree + ports; `info` iterates repos.

## M4.5 — Tests ✅ DONE
- [x] **M4.5.1** Registry + stack store unit tests (M4.0/M4.1).
- [x] **M4.5.2** `StackScopeBuilder` unit tests (M4.2).
- [x] **M4.5.3** Multi-repo create/teardown integration (M4.3 `WorkspaceStackTests`).

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