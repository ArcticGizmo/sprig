# M4 Verification — multi-repo stack (vue + dotnet)

Real end-to-end run driving the `Sprig.Cli` harness with actual git + docker.

## Setup
- `sprig-example-dotnet/.sprig.json` **provides** `baseUrl = http://localhost:${sprig.ports.api}`.
- `sprig-example-vue/.sprig.json` **consumes** it: env `VITE_API_URL = ${sprig.provides.dotnet-api.baseUrl}`.
- `sprig repo add` both → `sprig stack create web+api --repos sprig-example-vue,dotnet-api`.

## Results

| # | Exit criterion | Result |
|---|---|---|
| 1 | vue + dotnet in one workspace, cross-wired | `create demo --stack web+api` → frontend=20000, api=20001, postgres=20002; both worktrees created; **vue `.env` had `VITE_API_URL=http://localhost:20001`** (the dotnet API's allocated port, via `provides`); `up` → `librarydb_postgres--demo` running. ✅ |
| 2 | second concurrent workspace, no collision | `demo2` → frontend=20003, api=20004, postgres=20005; vue `VITE_API_URL=http://localhost:20004` (its *own* API); both postgres containers ran together (20002 & 20005). ✅ |
| 3 | teardown/reconcile all repos; source pristine | `rm --force` both → no containers/networks left; both source repos show only `?? .sprig.json`, `main` worktree only, no sibling folders. ✅ |
| 4 | stack export → import round-trip | Unit-verified in `StackStoreTests`. ✅ |
| 5 | tests green | 111 tests. ✅ |

## Notes
- Port **namespacing** (`<repo>.<port>`) means the two repos never collide even if they used the
  same port name; each repo's own scope sees local names, plus `provides.<repo>.<key>` for all.
- The `web+api` stack + both repo registrations remain in the machine-local central store
  (`%LOCALAPPDATA%\sprig`) — harmless, and demonstrates the registry/stack features. Remove with
  `sprig stack rm web+api` / `sprig repo rm <name>` if undesired.
- Left both `.sprig.json` files in the example repos (on-spec, revertible).
