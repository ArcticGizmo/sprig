# Claude worktree layout

**Status:** Shipped in v0.7.3. Picks up the "worktree location" item deferred in `docs/orchestration-primitives-plan.md`, narrowed to the one layout that has a concrete consumer: Claude Code.

## Why this exists

Sprig puts each worktree beside its repo (`<repo>--<ws>`). Claude Code puts its own under `<repo>/.claude/worktrees/<name>`, and a Claude session can only move between worktrees that live there. A sprig workspace is invisible to that: an agent cannot `EnterWorktree` into it once it is already in a worktree, and its session history is filed under a path Claude does not recognise as one of the repo's worktrees.

An opt-in layout puts sprig's worktrees where Claude expects them, and keeps today's sibling path as a junction so navigating by hand still works.

## What we verified (2026-10-30)

Tested against Claude Code on Windows with a real worktree and an NTFS junction each way round:

| Real worktree | Junction | `EnterWorktree` | Result |
|---|---|---|---|
| `sprig--jtest` (sibling) | `.claude/worktrees/jtest` | via the junction | Rejected: "not under `.claude/worktrees`" |
| `.claude/worktrees/jtest2` | `sprig--jtest2` (sibling) | via the junction | Accepted, resolved to the nested path |

- Claude Code resolves links before checking a worktree's location, so the **real** worktree must be the nested one. The sibling can only ever be the junction.
- `git rev-parse --show-toplevel` from a junction also returns the real path, so git, Claude and sprig all agree on one canonical location.
- Claude files session transcripts by the resolved path (`~/.claude/projects/<…>-sprig--claude-worktrees-<ws>/`), so a workspace has one history however it was entered. Auto-memory is shared across all worktrees of a repo regardless of layout.
- Junctions need no admin rights or Developer Mode.

## The rule

- **Opt-in, machine-wide.** `worktreeLayout` in `settings.json`: `sibling` (default, unchanged) or `claude`. It is a personal choice about this machine's tooling, so it does not belong in a committed `.sprig.json`.
- **Applies to new workspaces only.** The record stores the paths it was created with; nothing is migrated, and a workspace keeps its layout across claims, refreshes and switches.
- **The nested path is canonical.** `InstanceRepo.WorktreePath` is the real worktree. `sprig path`, `sprig cd`, `sprig code`, compose project directories and setup all use it.
- **The junction is a convenience.** `InstanceRepo.LinkPath` records it. Failing to create it warns and keeps the workspace; it never rolls a create back.
- **Never follow the junction when deleting.** Sprig removes a recorded link only after confirming it is a link, and removes the link itself, never its contents. A real directory sitting at the link path is left alone.

## Changes

### Core

- `SprigSettings.WorktreeLayout` (`Sibling` | `Claude`), cloned and persisted with the rest.
- `WorktreePaths.For(repoRoot, workspace, layout)` returns the worktree path and, for `claude`, the sibling link path. Create checks neither already exists.
- `DirectoryLink`: create a junction (Windows, via `FSCTL_SET_REPARSE_POINT`, no shell) or a symlink (elsewhere); report whether a path is a link and where it points; remove a link without following it.
- `GitExclude.Ensure(repoRoot, ".claude/worktrees/")` appends to the source repo's `info/exclude` when the path is not already ignored. Sprig still never edits a tracked file; without this the main checkout shows `?? .claude/worktrees/` and `git add -A` would stage the worktree as an embedded repo.
- `WorkspaceService` takes an optional `ISettingsStore`. Create reads the layout, adds the worktree at the nested path, ensures the exclude, then makes the junction. Rollback and `Remove` delete the junction before removing the worktree.
- `Locate` (behind `sprig here`) matches a directory under either the worktree or the link path, so a terminal sitting in `<repo>--<ws>` still resolves.
- `WorkspaceReconciler` reports the link alongside the worktree: `Ok`, `Missing`, `WrongTarget` or `Blocked` (something that is not a link is in the way). Repair recreates a missing link and re-points a wrong one; it never touches `Blocked`.
- `InitInspector` stops descending into nested checkouts (a directory with its own `.git`), so env/compose discovery in the main repo ignores `.claude/worktrees/*`.

### CLI

- `sprig settings show` prints the layout; `sprig settings set --worktree-layout sibling|claude` changes it.
- `doctor`/`reconcile` print link drift and repairs.

### App

- Settings gets a **Worktrees** panel with a checkbox to use Claude Code's layout, saved immediately like the changelog toggle.
- The port-policy Save builds on the stored settings instead of a fresh object, so it no longer resets the other settings (changelog toggle, completed guides, dismissed update), including the new layout.

## Hazards to document

A worktree nested inside the repo is visible to anything that walks the main checkout and does not honour `.gitignore`: a repo-root project that globs `**/*.cs`, a root `tsconfig` with `"include": ["**/*"]`, a Docker build context of `.` without a `.dockerignore`, and file watchers. Claude Code's own worktrees already have this property, so a repo that works with them works with this layout.

## Deferred

- Migrating an existing workspace between layouts.
- Linking local, untracked `.claude` state (`settings.local.json`, local skills) into each worktree. Separate problem, separate plan.
- Other layout templates. Only add one when something needs it.
