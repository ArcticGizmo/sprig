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

## M5.1 — CLI `init`
- [ ] **M5.1.1** `init [--repo <path>] [--print] [--force]` (default repo = cwd). Runs the
      inspector, prints the proposal + notes.
- [ ] **M5.1.2** Write `.sprig.json` unless it exists (then require `--force`); `--print` writes
      nothing (dry-run to stdout).
- [ ] **M5.1.3** After writing, hint the next steps (`sprig repo add`, `sprig create`).

## M5.2 — Authoring ergonomics
- [ ] **M5.2.1** `init` optionally `--register` (auto `repo add` after writing).
- [ ] **M5.2.2** Friendlier empty-states / errors already present in `ls`/`create`; audit and
      fill gaps (e.g. `create --stack` with an unregistered repo → actionable message).

## M5.3 — Tests ✅ DONE (detection)
- [x] **M5.3.1** 7 `InitInspector` tests: bare-port env, embedded-port note, compose
      container_name+port, named-volume note, folder name, dedup, `ParseEnv`. (CLI no-overwrite
      covered by the M5.1 walkthrough.)

## M5.4 — Verification
- [ ] **M5.4.1** Run `init --print` against both example repos; confirm the proposal matches the
      hand-written `.sprig.json` shape (ports/env/compose). Note in `docs/m5-verification.md`.

---

## Notes
- Detection is heuristic and advisory — always a *proposal* the user edits. Don't over-engineer
  framework sniffing; port-shaped env keys + compose structure carry most of the value.
- **New Core:** `Init/` (`InitInspector`, `InitProposal`).
- Commit per sub-milestone; local only.