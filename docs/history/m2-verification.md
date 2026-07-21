# M2 Verification — first functional slice against `sprig-example-vue`

Real end-to-end walkthrough driving the `Sprig.Cli` harness against the actual example repo.
All four exit criteria met.

## Setup
Authored the one file sprig adds to a repo — `sprig-example-vue/.sprig.json` (schema 1):
port `frontend`, env override `.env → PORT=${sprig.ports.frontend}`, `provides.baseUrl`.

## Results

| # | Exit criterion | Result |
|---|---|---|
| 1 | vue runs isolated on an allocated port | `sprig create feat-a` → worktree `…--feat-a`, branch `sprig/feat-a`, `frontend=20000`. `.env` clobbered (top+bottom `PORT=20000`, seeded `6010` between). **Vite bound `http://localhost:20000`** (booted with a `node_modules` junction; no reinstall). ✅ |
| 2 | teardown leaves source pristine | after `rm feat-a --force` + `rm feat-b --force`: `git status` shows only `?? .sprig.json`; `.env` still `PORT=6010`; no `sprig/*` branches; only the `main` worktree; no sibling folders. ✅ |
| 3 | reconcile repairs drift | `feat-b` (port `20001`, non-colliding). Deleted its worktree folder → `reconcile` reported `[DRIFT] MissingFolder` → `reconcile --repair` pruned the stale registration. (Drift B orphan-removal covered by tests.) ✅ |
| 4 | tests green | 78 tests pass. ✅ |

## Notes
- **`.env` is git-tracked** in this repo, so the worktree receives it and sprig clobbers the
  worktree copy — the source copy is never touched (confirmed).
- Left `sprig-example-vue/.sprig.json` in place — it is the intended, on-spec artefact that makes
  the repo sprig-ready. Trivially revertible if not wanted.
- **Windows junction hazard learned:** a `node_modules` **junction** inside a worktree must be
  unlinked with `rmdir` (never a recursive delete that could follow it into the source). Sprig's
  own teardown uses `git worktree remove --force` + .NET `Directory.Delete`, which handle reparse
  points without recursing — but worth keeping in mind for any future "copy/link deps" setup step.
