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

## M6.0 — App scaffold + shell + theme
- [ ] **M6.0.1** `src/Sprig.App/Sprig.App.csproj` (`net10.0`, `WinExe` on Windows/`Exe` else,
      Avalonia 12 + Desktop + Themes.Fluent + Fonts.Inter + CommunityToolkit.Mvvm + Avalonia.Headless;
      compiled bindings on; references `Sprig.Core`). Add to `sprig.slnx`.
- [ ] **M6.0.2** `Program.cs` (BuildAvaloniaApp + classic desktop lifetime; `render <dir>` arg for
      headless PNG dump), `App.axaml(.cs)` (Dark Fluent + shared palette from perch), `app.manifest`.
- [ ] **M6.0.3** `MainWindow` shell: left nav (Workspaces / Repos / Stacks) + content region;
      dark palette; `ViewLocator` for VM→View.
- [ ] **M6.0.4** Builds and launches to an empty shell (verified via headless render).

## M6.1 — Composition root (`AppServices`)
- [ ] **M6.1.1** `AppServices` wires the real Core graph (`SprigPaths`, `ProcessRunner`,
      `GitService`, `FilePortStore`, `InstanceStore`, `EnvClobberService`, `ComposeGenerator`,
      `DockerService`, `WorkspaceService`, `WorkspaceReconciler`, `RepoRegistryStore`, `StackStore`,
      `StackResolver`, `InitInspector`).
- [ ] **M6.1.2** An async run helper: Core calls execute on a background thread; results/errors
      marshalled back for the VM (UI never blocks on git/docker).

## M6.2 — Workspaces (list + detail + lifecycle)
- [ ] **M6.2.1** `WorkspacesViewModel`: observable list from `InstanceStore`; refresh; select.
- [ ] **M6.2.2** Detail: repos, per-repo ports, branch, worktree path, live infra status + drift
      (via reconciler), last status.
- [ ] **M6.2.3** Lifecycle async commands: Up / Down / Reset / Reconcile(+repair) / Open worktree
      (explorer/editor) / Remove (with confirm + force).
- [ ] **M6.2.4** Busy/error surfacing (spinner while a command runs; error banner on failure).

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