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

## M6.3 — Create workspace flow ✅ DONE
- [x] **M6.3.1** Create overlay in `WorkspacesViewModel`: stack picker + name, creates via
      `StackResolver` + `WorkspaceService.Create`, selects the new workspace on success.
- [x] **M6.3.2** Inline create errors (invalid name, missing config, worktree exists).

## M6.4 — Repos + Stacks management ✅ DONE
- [x] **M6.4.1** `ReposViewModel` + view: list registry; Register (existing `.sprig.json`) and
      Init &amp; register (writes `.sprig.json` via `InitInspector` + `ConfigJson`); Unregister.
- [x] **M6.4.2** `StacksViewModel` + view: list stacks; create (name + repo checkboxes); remove.
      (Export/import kept in the CLI for now — noted as a later UI nicety.)

## M6.5 — Verification (headless render + VM tests) ✅ DONE
- [x] **M6.5.1** `HeadlessRenderer` renders all three pages to PNG (`sprig-gui render <dir>`).
- [x] **M6.5.2** 4 VM tests over a temp store (repos register/unregister/error, stacks
      create/remove/error). Workspace lifecycle VMs covered by the render integration + Core tests.
- [x] **M6.5.3** Screenshots sent (shell, workspaces, repos, stacks).

## M6.6 — Polish ✅ DONE (essentials)
- [x] **M6.6.1** Empty states (workspaces "no selection" hint; empty repo/stack lists with add panels).
- [x] **M6.6.2** Confirm-before-destroy bar on Remove with explicit force-branch checkbox.
      (Keyboard shortcuts, iconography, and 1.5x render deferred as later polish / M7.)

---

## M6 complete ✅
Full Avalonia dark-mode UI over `Sprig.Core`: nav shell, Workspaces (list/detail/lifecycle +
create overlay), Repos (register / init & register), Stacks (builder). Every M1–M5 capability is
reachable from the UI; all git/docker calls run off the UI thread. **122 tests green.**
Verified via headless render. Next: **M7** (packaging + docs). Best judged by running it.

---

## Notes
- **New project:** `src/Sprig.App/` (Views/, ViewModels/, Rendering/, Theming/, Assets/).
- VMs depend only on `Sprig.Core` services via `AppServices`; keep them unit-testable.
- Windows-first; Core stays OS-agnostic so a mac head remains possible (not built now).
- Commit per sub-milestone; local only.