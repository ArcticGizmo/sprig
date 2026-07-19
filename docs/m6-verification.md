# M6 Verification — Avalonia UI

Verified via headless render (`sprig-gui render <dir>`) against the real central store, plus
ViewModel unit tests.

## Screens (headless-rendered, dark Fluent)
- **Shell** — branded left nav (Workspaces / Repos / Stacks), accent-highlighted selection.
- **Workspaces** — master list + detail: per-repo cards (allocated ports, `sprig/<ws>` branch,
  worktree path, drift state), lifecycle toolbar (Up / Down / Reset / Reconcile / Repair /
  Open folder / Remove), a confirm-teardown bar with a force-branch checkbox, and a
  "New workspace" overlay (stack picker + name).
- **Repos** — register by path (Register / Init &amp; register) + registry list + Unregister.
- **Stacks** — existing stacks (with their repos) + a "New stack" builder (name + repo checkboxes).

Rendered `demo` (web+api) correctly showed `frontend=20000`, `api=20001`, `postgres=20002` and
both worktree paths — i.e. the VM→Core path produces correct live data.

## Exit criteria
1. Every M1–M5 capability reachable from the UI (register → stack → create → up/down/reset/
   reconcile/repair/open/remove). ✅
2. Dark theme; all git/docker calls run off the UI thread via `AppServices.RunAsync`. ✅
3. Headless screenshots produced; 4 VM unit tests green. ✅
4. Solution builds; **122 tests green** (118 Core + 4 VM). ✅

## Notes
- The app is its own exe (`sprig-gui`). Wiring a `sprig gui` launcher from the CLI is deferred to
  M7 (packaging), where both ship together.
- Best judged by running it (`dotnet run --project src/Sprig.App`) — expect a round of visual
  feedback on spacing/wording/iconography.
- Export/import stacks and folder-pickers remain CLI-only for now (later UI niceties).
