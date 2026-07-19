# M3 Verification — docker infrastructure against `sprig-example-dotnet`

Real end-to-end run with actual docker (29.5.3), driving the `Sprig.Cli` harness.

## Setup
Authored `sprig-example-dotnet/.sprig.json` (schema 1): ports `api` + `postgres`; env `.env`
(`PORT`, and `ConnectionStrings__Default` with `Port=${sprig.ports.postgres}`); compose
overrides suffixing `container_name` and remapping `ports[0]` → `${sprig.ports.postgres}:5432`.

## Results

| # | Exit criterion | Result |
|---|---|---|
| 1 | fully isolated infra | `create feat-a` → `api=20000, postgres=20001`; `up` → container **`librarydb_postgres--feat-a`** on host port **20001**, network `sprig-feat-a_default`; `psql` reached `librarydb`. Worktree `.env` connection string = `Host=localhost;Port=20001;…` (**matches** the allocated port). Generated compose in `%LOCALAPPDATA%\sprig\instances\feat-a\`. ✅ |
| 2 | concurrent, non-colliding | `feat-b` → `api=20002, postgres=20003`; both postgres containers ran together with distinct names/ports/networks. ✅ |
| 3 | teardown clean | `rm --force` (uses `down -v`) removed all containers, networks, worktrees, branches, records; source repo pristine (only `.sprig.json` untracked, `.env` unchanged, `main` worktree only). ✅ |
| 4 | tests green | 93 tests. ✅ |

## Volume-persistence caveat (honest finding)
The objective's "`down` keeps volumes, teardown wipes" holds **for named volumes**. The example
compose declares **no named volume**, so postgres data sits in an *anonymous* volume that docker
does not reattach when the container is recreated — so a row written before `down` is **not**
visible after `up`, regardless of sprig. This is standard docker behavior, not a sprig defect:
- sprig issues `down` (keep) vs `down -v` (wipe) correctly — unit-verified in `WorkspaceInfraTests`;
- teardown always uses `down -v`.

**Implication for M5 `init`:** when detecting infra, sprig should encourage a named volume (or
surface that data won't persist across `down`) so the "keeps volumes" promise is meaningful.

## Notes
- Left `sprig-example-dotnet/.sprig.json` in place (on-spec, revertible).
- No relative-path/bind-mount in this compose, so the S2 `--project-directory` behavior wasn't
  re-exercised here (already proven in S2 with an injected bind mount).
