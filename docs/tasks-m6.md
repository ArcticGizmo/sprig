# Sprig — Task Breakdown: M6 (Avalonia UI — the real deliverable)

**Milestone goal:** the intuitive desktop UI from the objective — dark mode, `perch` conventions
(.NET 10 · Avalonia 12 · Fluent · Inter · CommunityToolkit.Mvvm) — layered as a thin `Sprig.App`
head over the proven `Sprig.Core`. **No logic in the UI**; ViewModels call Core services off the
UI thread.

**Exit criteria:**
1. Every M1–M5 capability is reachable from the UI: see/define repos, define stacks, create a
   workspace, and per-workspace lifecycle (up/down/reset/open/teardown) with drift/reconcile.
2. Dark theme; responsive (git/docker calls never block the UI thread).
3. Headless-render screenshots produced for verification; ViewModel logic unit-tested.
4. Solution builds; existing 118 Core tests stay green.

**Judgement note:** the look/feel is best judged by running it — I'll build + headless-render +
send screenshots, but expect a round of visual feedback from you.

---

## M6.0 — App scaffold + shell + theme ✅ DONE
- [x] **M6.0.1** `Sprig.App.csproj` (net10.0, WinExe, Avalonia 12 + Desktop + Fluent + Inter +
      Headless + CommunityToolkit.Mvvm, compiled bindings, refs Core); in `sprig.slnx`.
- [x] **M6.0.2** `Program` (+ `render` arg), `App.axaml(.cs)` (Dark Fluent + palette), `app.manifest`.
- [x] **M6.0.3** `MainWindow` shell (branded left nav + content), `ViewLocator` VM→View.
- [x] **M6.0.4** Builds; headless render confirms the dark shell + nav highlight.

## M6.1 — Composition root (`AppServices`) ✅ DONE
- [x] **M6.1.1** `AppServices` wires the full real Core graph.
- [x] **M6.1.2** `AppServices.RunAsync` runs blocking Core calls off the UI thread.

## M6.2 — Workspaces (list + detail + lifecycle) ✅ DONE
- [x] **M6.2.1** `WorkspacesViewModel` observable list from `InstanceStore`; refresh; select.
- [x] **M6.2.2** Detail: per-repo cards (ports, branch, worktree path, drift state); status/drift line.
- [x] **M6.2.3** Async Up/Down/Reset/Reconcile/Repair/Open-folder/Remove (with confirm bar + force).
- [x] **M6.2.4** Indeterminate progress while busy; green status / red error text. Verified via render.

## M6.3 — Create workspace flow
- [ ] **M6.3.1** `CreateWorkspaceViewModel`: pick a stack (or ad-hoc repo path), enter a name
      (validated), create; on success select the new workspace.
- [ ] **M6.3.2** Surface create errors (invalid name, missing config, worktree exists) inline.

## M6.4 — Repos + Stacks management
- [ ] **M6.4.1** `ReposViewModel`: list registry; add repo (folder picker) with optional `init`
      first; remove. Show detected/validated config summary.
- [ ] **M6.4.2** `StacksViewModel`: list stacks; create (pick registered repos, add vars); remove;
      export/import (file pickers).

## M6.5 — Verification (headless render + VM tests)
- [ ] **M6.5.1** `HeadlessRenderer` renders the main views (with synthetic data) to PNG at 1x/1.5x.
- [ ] **M6.5.2** ViewModel unit tests over a temp store + fake git/docker (list/create/remove/
      reconcile transitions) — no real UI needed.
- [ ] **M6.5.3** Send screenshots; capture visual feedback.

## M6.6 — Polish
- [ ] **M6.6.1** Empty states (no workspaces/repos/stacks), consistent iconography, keyboard basics.
- [ ] **M6.6.2** Confirm-before-destroy on Remove (esp. `--force` branch deletion).

---

## Notes
- **New project:** `src/Sprig.App/` (Views/, ViewModels/, Rendering/, Theming/, Assets/).
- VMs depend only on `Sprig.Core` services via `AppServices`; keep them unit-testable.
- Windows-first; Core stays OS-agnostic so a mac head remains possible (not built now).
- Commit per sub-milestone; local only.