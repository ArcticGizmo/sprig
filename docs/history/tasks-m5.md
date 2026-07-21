# Sprig — Task Breakdown: M5 ("Easy to configure" onramp)

**Milestone goal:** make a repo sprig-ready with minimal effort. The centrepiece is **`init`**:
detect a repo's isolation surface (env port keys, compose services/ports/container names,
named volumes) and propose a `.sprig.json`. The registry + stack authoring already landed in
M4, so M5 focuses on detection + authoring ergonomics + the named-volume hint from M3.

**Exit criteria:**
1. `sprig init --repo <path>` on a fresh clone of `sprig-example-vue` / `sprig-example-dotnet`
   proposes a `.sprig.json` close to the hand-written ones (ports, env, compose overrides).
2. `init` never overwrites an existing `.sprig.json` without `--force`; `--print` dry-runs.
3. Compose named-volume detection surfaces the "won't persist across `down`" hint (M3 finding).
4. `dotnet test` green (detection logic unit-tested over fixtures).

**Builds on:** M1 config model, M3 `ComposeGenerator`/YamlDotNet, M4 registry/stack CLI.

---

## M5.0 — Detection engine (`InitInspector`) ✅ DONE
- [x] **M5.0.1** `InitInspector.Inspect` → `InitProposal(Config, Notes)`; name from folder.
- [x] **M5.0.2** Env: bare-int-in-range keys → named port + `KEY=${sprig.ports.<name>}`;
      connection-string/URL values → advisory note (no auto-rewrite).
- [x] **M5.0.3** Compose: per-service `container_name` suffix + first port → named port `<svc>`
      + `ports[0]` override.
- [x] **M5.0.4** Named-volume note (M3 hint).
- [x] **M5.0.5** Port-name dedup across env + compose.

## M5.1 — CLI `init` ✅ DONE
- [x] **M5.1.1** `init [--repo <path>] [--print] [--force] [--register]`; prints proposal + notes.
- [x] **M5.1.2** Writes `.sprig.json` unless present (then `--force`); `--print` = dry-run.
- [x] **M5.1.3** Prints next-step hints (`repo add` / `create`).

## M5.2 — Authoring ergonomics ✅ DONE
- [x] **M5.2.1** `init --register` auto-registers after writing.
- [x] **M5.2.2** Empty-states/actionable errors present across `ls`/`create`/`init`/`repo`/`stack`
      (e.g. unregistered repo in a stack → `StackException` naming it).

## M5.3 — Tests ✅ DONE (detection)
- [x] **M5.3.1** 7 `InitInspector` tests: bare-port env, embedded-port note, compose
      container_name+port, named-volume note, folder name, dedup, `ParseEnv`. (CLI no-overwrite
      covered by the M5.1 walkthrough.)

## M5.4 — Verification ✅ DONE (see `docs/m5-verification.md`)
- [x] **M5.4.1** `init --print` on both example repos matched the hand-written shape (ports/env/
      compose), with notes flagging the dotnet connection-string/URL embedded ports; write /
      no-overwrite / `--force` verified on a throwaway repo.

---

## M5 complete ✅
`sprig init` detects a repo's isolation surface and proposes a `.sprig.json` (write / `--print` /
`--force` / `--register`); registry + stack authoring (from M4) round out the onramp.
**118 tests green.** Next: **M6** (Avalonia UI — the real deliverable).

## Notes
- Detection is heuristic and advisory — always a *proposal* the user edits.
- **New Core:** `Init/` (`InitInspector`, `InitProposal`).