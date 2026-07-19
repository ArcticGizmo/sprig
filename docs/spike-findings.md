# M0 Spike Findings

Results of the de-risking spikes from [`tasks-m0-m1.md`](./tasks-m0-m1.md). Each section
records what was tested, the result, and any impact on
[`implementation-plan.md`](./implementation-plan.md).

Environment: docker 29.5.3, .NET SDK 10.0.302, node v22.19.0 / npm 11.6.4.
Throwaway spike code lived in the session scratchpad (not committed).

---

## S1 — `.env` clobber wins in Vite *and* DotNetEnv ✅ DONE

**Question:** when a key appears twice in one `.env` file, does the parser take the **first**
or **last** occurrence — or **error**? (An error would break the top+bottom trick.) And does
the targeted file beat a sibling `.env`?

**Method:** exercised the *actual* code paths, not reimplementations.
- **DotNetEnv** — throwaway console referencing the real `DotNetEnv` package, replicating the
  app's `Env.Load(".env")` then `Env.Load(".env.local")` (`Program.cs:7-8`).
- **Vite** — called `loadEnv('development', dir, '')` directly (exactly what
  `sprig-example-vue/vite.config.ts` does), against a fixture dir.
- Fixture: `.env` had `PORT=9999` + `BASE_ONLY`; `.env.local` had `PORT=1111` (top) …
  `PORT=2222` (bottom), plus a duplicated `DUP` key.

**Results:**

| Behaviour | DotNetEnv | Vite (`loadEnv`) |
|---|---|---|
| Duplicate key in one file | **last wins** (`PORT=2222`, `DUP=second`) | **last wins** (`PORT=2222`, `DUP=second`) |
| Errors on duplicate keys? | **No** | **No** |
| Targeted file vs sibling `.env` | `.env.local` overrides `.env` (2nd `Load` wins) | `.env.local` overrides `.env` |
| Non-overridden sibling keys | preserved | preserved (`BASE_ONLY=frombase`) |

**Conclusion / recipe (locked):**
- Both target frameworks are **last-wins** and **tolerant of duplicate keys**, so writing the
  sprig block at **top *and* bottom** guarantees a win under *both* first-wins and last-wins
  loaders, and won't error on these two frameworks.
- Sprig writes to the **targeted file** (e.g. `.env.local`), which already beats a sibling
  `.env` — so we don't need to touch `.env` to win. (Confirms the "only targeted files"
  decision.)
- **Injection layout:** `# >>> sprig >>>` block (all overridden keys) at the very top, the
  seeded original content in the middle, and an identical `# <<< sprig <<<` block at the
  bottom.
- **Edge case for later:** if a future framework *errors* on duplicate keys (neither of these
  does), fall back to single-position injection at the last line, or strip-and-replace the
  original key. Not needed for v1 targets.

**Impact on plan:** none — the plan's §3.2 mechanic holds as written.

---

## S2 — Centrally-stored compose via `--project-directory` ✅ DONE

**Question (biggest design risk):** can the generated compose live *only* in the central
store, run against a worktree, and still resolve relative paths (bind mounts, build contexts)
correctly? And does per-workspace project-name scoping isolate two instances?

**Method:** real docker (29.5.3). Created a git worktree of the dotnet repo
(`sprig-example-dotnet--spike`), a central compose at
`…/central/sprig-spike/docker-compose.sprig.yml` with overrides applied by hand
(`container_name` suffixed, `ports[0]` → `25432:5432`) **plus a relative bind mount**
`./initdb/init.sql:…:ro`. The `init.sql` lived **only in the worktree**. Ran with
`docker compose -f <central>/…yml --project-directory <worktree> -p sprig-spike`.

**Results:**
- `compose config` resolved the relative mount to the **worktree** path
  (`…\sprig-example-dotnet--spike\initdb\init.sql`) — *not* the central file's dir. ✅
- At runtime the worktree-mounted `init.sql` **executed** — querying the DB returned the
  marker row `resolved-against-worktree`. ✅ (Proves the mount resolved correctly end-to-end.)
- Container came up with the suffixed name and remapped host port `25432`. ✅
- **Isolation:** a second instance (`-p sprig-spike2`, port `25433`, name suffix `spike2`)
  ran concurrently with separate containers, ports, and networks
  (`sprig-spike_default` vs `sprig-spike2_default`). ✅
- **Teardown:** `down` (keeps volumes) and `down -v` (wipes) both worked by project name;
  no containers/networks left behind. ✅

**Conclusion:** the central-only generated-compose model is validated. The `--project-directory
<worktree>` flag is **mandatory** and must always point at the worktree so relative paths
resolve there. Per-workspace `-p sprig-<workspace>` gives baseline network/volume isolation
for free, on top of the explicit name/port overrides.

**Impact on plan:** none — §3.3 holds. Reinforced requirement: sprig must *always* pass
`--project-directory <worktree>` on every compose invocation (up/down/ps/config).

**Windows note:** Git Bash mangles container-internal paths passed to `docker exec`
(MSYS path conversion, e.g. `/docker-entrypoint-initdb.d/...`). Irrelevant to sprig itself
(it shells out from .NET, not Git Bash) but worth remembering when hand-testing.

## S3 — git worktree lifecycle + drift on Windows ✅ DONE

**Question:** how does `git worktree` behave on Windows for create/remove and the two drift
cases the objective demands tolerance for, and what exact commands reconcile each?

**Method:** real git against `sprig-example-dotnet`, walking each scenario.

**Facts established:**
- `git worktree add <path> -b sprig/<ws>` off `HEAD` gives a clean checkout with **no `.env`**
  (untracked files aren't copied) — confirms **seeding is required** (§3.2).
- A worktree's `.git` is a **file**, not a dir: `gitdir: <repo>/.git/worktrees/<name>`.
- `git worktree list --porcelain` is the parse source; it emits `worktree`/`HEAD`/`branch`
  lines per entry **and a `prunable` line** when the folder is missing.
- `git worktree remove` **refuses (exit 128)** when the worktree has untracked/modified files.
  Sprig worktrees always have a clobbered `.env`, so **sprig must use `remove --force`**.
- Removing a worktree **keeps the branch** (`sprig/<ws>` survived) — matches the "keep branch
  unless `--force`" decision; delete the branch as a separate, explicit step.

**Reconciliation matrix (drives M2's `reconcile`/teardown):**

| State | Detection | Action |
|---|---|---|
| Healthy | in `list`, not `prunable`, folder exists | `worktree remove --force` |
| **Drift A** — folder deleted, admin remains | in `list`, flagged **`prunable`** | `worktree prune` |
| **Drift B** — admin gone, folder remains (objective's example) | **not** in `list`, folder on disk (per central record) | plain `rm -rf` folder (nothing registered to corrupt) |
| Both gone | not in `list`, not on disk | no-op |

- Drift A verified: `prune --dry-run -v` reports *"gitdir file points to non-existent
  location"*; `prune` clears the stale entry.
- Drift B verified: with the admin entry deleted, `list` omits the folder and `prune` finds
  nothing, so detection must come from **sprig's own central record** cross-checked against
  `list` + disk.

**Windows gotchas:**
- `remove` needs `--force` (untracked `.env`) — the main one; already designed for.
- **Locked files** (a running dev server / docker holding `node_modules`, `bin/obj`) can block
  folder removal. Teardown must **stop infra (and any spawned processes) first**, and should
  **retry with backoff** on `rm`. (Flagged; not exhaustively reproduced.)
- **Long paths:** worktree path + `node_modules` can exceed 260 chars. Enable long-path
  support / use extended-length paths from .NET. (Flagged.)

**Impact on plan:** none conceptually; §3.4 holds. Adds concrete requirements for M2:
`remove --force`, parse `--porcelain` incl. `prunable`, and the 4-state matrix above.

---

## M0 summary

All three spikes passed with **no design changes** to `implementation-plan.md`. The biggest
risk (S2, central-only compose) is cleared. Ready to start **M1 — Core spine**.

Concrete requirements harvested for later milestones:
- **M2:** always `git worktree remove --force`; parse `worktree list --porcelain` including
  `prunable`; implement the 4-state reconciliation matrix; stop infra/processes before folder
  removal, retry `rm` on lock; keep branch unless forced.
- **M3:** always pass `--project-directory <worktree>` and `-p sprig-<workspace>` on every
  compose call.
- **Env writer:** top+bottom marker block in the targeted file(s); values identical top/bottom.
