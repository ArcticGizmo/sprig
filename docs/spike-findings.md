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

## S2 — Centrally-stored compose via `--project-directory` ⏳ TODO

## S3 — git worktree lifecycle + drift on Windows ⏳ TODO
