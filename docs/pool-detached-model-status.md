# Pool detached-model + start-point picker + branch graph — STATUS / HANDOFF

**As of:** 2026-08-13 · **Branch:** `pools` (all work committed locally, nothing pushed) ·
**Companion:** `docs/pool-detached-model-plan.md` is the authoritative design/model description — read it
first. This doc is the "where we are + what's left" handoff.

## TL;DR

Three bodies of work are built, tested (**664 tests green**), and committed on `pools`:

1. **Detached-slot / branch-on-claim pool model** — idle slots park in detached HEAD; claiming cuts a
   user-named branch across the stack; label is optional; release reports pending work and touches no git.
2. **Keep/fresh + start point** — `ResolveDefaultBase` prefers `upstream` over `origin`; a searchable,
   fetch-instant "Start from" dropdown (chips, recent-by-default) with `--from <ref>` in the CLI.
3. **Branch graph** — a resizable GitKraken-style window: per-row swimlane rendering, wrapped messages,
   coloured branch chips, click-to-select, current branch ringed, search-to-jump.

## Commit trail (on `pools`, newest first)

```
26cc8c0 Branch graph: fix lane tearing — fixed-width sha/age column
bfecb80 Branch graph: per-row rendering — wrapped messages, roomier rows, chip-outline fix
2b24c1c Branch graph: resizable window, wrapped rows, left-side sha/age, selected chip outline
a513cc5 Refine the branch graph: bigger dots, select-to-highlight, coloured pills, search-jump
160350d Add a visual branch graph (GitKraken-style) to the start-point picker
caa60e3 Start-point selection: prefer upstream base + searchable branch picker
ade2bd1 Make the start-from picker instant: local-first, background fetch, cache
4c9d04e Detached-slot pool model: branch-on-claim, keep/fresh handling
```
(`ade2bd1` sits between the two start-point commits — the picker-perf change.)

## Where things live (the map)

- **Core model:** `src/Sprig.Core/Workspaces/WorkspaceService.cs` (Create parks detached) +
  `WorkspaceService.Claim.cs` (Claim, CutBranchAndStart, CollectPending, StartPoints, StartPointFilter,
  CommitGraphData). `src/Sprig.Core/Pools/PoolService.cs` (Checkout/Release/StartPointsFor/CommitGraphFor,
  `CheckoutMode { Keep, Fresh }`).
- **Git layer:** `src/Sprig.Core/Git/GitService.cs` + `IGitService.cs` — AddWorktreeDetached, SwitchNewBranch,
  ResolveDefaultBase (upstream-preferring), ListStartPointCandidates, CurrentBranch, RefExists,
  ListCommitGraph, HasUncommittedChanges/CountUnpushedCommits, IsValidBranchName.
- **Graph layout (pure, tested):** `src/Sprig.Core/Graph/CommitGraphLayout.cs`
  (GraphNode/GraphLink/GraphRowRender/GraphSegment; per-row swimlane cells).
- **App:** `RowGraphControl.cs` (per-row drawing), `BranchGraphWindow.axaml(.cs)` (resizable dialog),
  `WorkspacesViewModel.cs` (checkout + picker + graph state/commands), `GraphRowViewModel` /
  `GraphRefViewModel` / `GraphConverters` / `StartPointItemViewModel` / `RelativeTime`,
  `Views/WorkspacesView.axaml(.cs)` (checkout overlay + graph-icon button + window wiring).
- **CLI:** `src/Sprig.Cli/Commands/PoolCommands.cs` (`--branch` required, `--label` optional,
  `--keep`/`--fresh`, `--from <ref>`, interactive pickers).
- **Tests:** `tests/Sprig.Tests/` — Pools/, Workspaces/ (incl. StartPointTests), Graph/
  (CommitGraphLayoutTests), Git/GitServiceTests, App/PoolWorkspaceViewModelTests.

## Remaining work

### Should do to call it "done"

1. **Real end-to-end lifecycle test (Docker) — highest value, not covered by CI.** Tests stub Docker with a
   fake. Validate on a real stack: **keep** preserves DB/volumes across re-checkout; **fresh** wipes volumes
   + reinstalls deps; a default checkout branches from **`upstream/main`** (not the fork's stale main).
2. **Update user-facing docs to the new model.** `docs/pooled-workflow.md` and `docs/user-guide.md` still say
   "label required" and "as-is / fresh / refresh". Now it's: branch required + optional label, **keep/fresh**,
   the start-point picker (default upstream), and the branch graph. (`docs/pool-model-plan.md` is the older
   milestone plan — leave as history or annotate.)
3. **Final visual sign-off on the graph** after the tearing fix (`26cc8c0`): lane straight, dots aligned to
   the message line. The one blind constant is the dot's vertical offset — `RowGraphControl.DotLineCenter`
   (17px) vs the message's ~7px top padding in `BranchGraphWindow.axaml`.
4. **Migration check:** any workspace from the *old* `sprig--<workspace>` model should be torn down
   (`sprig ws rm --force`) and recreated — the new model is not migrated in place.

### Optional / deferred (noted in code + design doc)

- **Remote-logo chips** — show a host logo (GitHub/GitLab/…) circle instead of the `origin/` prefix. Parked
  (needs remote-URL host detection + embedded logos + offline fallback). A lightweight version: strip the
  remote prefix and show a small per-remote coloured dot.
- **Advanced per-repo start point** — a different start ref per repo in a multi-repo stack. `TODO` on
  `WorkspaceService.Claim`. Related: the branch graph shows only the **first repo** of a multi-repo stack.
- **Reattach to an existing branch** — claim always cuts a *new* branch and blocks if the name exists; there
  is no "resume this exact branch" flow.
- **Changelog + version bump** — significant user-facing change; use the `bump-version` skill for a release
  entry.
- **Merge `pools` → `main`** — user's call (this repo's convention is commit-locally-only; do not push).

## Gotchas for a fresh context

- **GUI lock:** the running `sprig-gui` holds `Sprig.App`'s DLL/exe, so `dotnet build`/`dotnet test` on the
  App fails with a file-copy (MSB3021/MSB3027) error until the app is closed. That's a *copy* failure, not a
  compile failure — Core/CLI build fine independently, and Avalonia compiles XAML at build so a green build
  means the XAML is structurally valid.
- **No desktop screenshots:** there's no tool here to screenshot the desktop Avalonia app; visual iteration
  is user-driven (they screenshot / describe).
- **Commit-locally-only:** per project convention, commit but never push or delete branches.
- **CRLF warnings** on commit are benign (Windows line-ending normalization).
- **Editing control chars:** don't hand-type `\uXXXX` into files via tools (it gets interpreted). Use e.g.
  `(char)0x1f` (see `GitService.ListCommitGraph`) or git's own `%x1f` in format strings.
- **Run all tests:** `dotnet test tests/Sprig.Tests/Sprig.Tests.csproj` (needs the GUI closed; ~3 min).

## Model quick-reference (see plan doc for full detail)

- Idle slot = detached HEAD at base; **claim** cuts one branch across the stack (the identity); label optional.
- **keep** vs **fresh**: both cut a clean branch from the start point and reset tracked files to it (gitignored
  artifacts survive). keep leaves deps + volumes (fast); fresh reinstalls deps + wipes volumes.
- **Start point** default = each repo's base, upstream-preferred; overridable via picker (`startPoint` is one
  ref across the stack, per-repo fallback to base when absent).
- **Release** = report pending work (uncommitted / unpushed), stop containers, touch no git.
