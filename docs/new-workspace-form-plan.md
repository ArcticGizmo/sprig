# New workspace form, Free, and a remove that finishes

Branch: `experimental`. Written 2026-11-10 from the user's feedback after using slots.

## Feedback (user, 2026-11-10)

1. Removing a workspace left its folder behind, so the next check-out asked to fix the slot.
2. The form's **Starting from** isn't interactive, so it's hard to see which branches can be picked, especially with several remotes. Its order doesn't fit what you're doing. There are four cases:
   - set a slot up with no branch (detached);
   - a new branch from the default branch;
   - adopt an existing branch (check out `feat/xxx`);
   - a new branch from a chosen branch.
3. There should be an option other than Remove that keeps the worktree on a detached HEAD, to be claimed later. Claiming still asks what to reset, but it's quicker because the expensive work is done.

## Why the folder stayed (1), fixed 2026-11-10

`git worktree remove --force` deletes the folder first, and forgets the worktree once it has started. On Windows, a file something still has open stops the delete partway. That could be an app that has only just stopped, an IDE, or a build server. Git exits 255 with the folder half deleted and no longer listed as a worktree. Asking git again then fails with "is not a working tree", and the folder stays in the slot's way. This was reproduced on a scratch repo.

Remove now deletes whatever git leaves itself (links are removed, not followed). It tries a few times while the last app lets go, then prunes git's list. If the folder still can't be deleted, it says what's held and keeps the slot, and removing again once that's closed finishes the job. Commit 8fc7e7b.

## Decisions (user, 2026-11-10)

| Topic | Decision |
|---|---|
| Form | **Mode first.** Choose **New branch**, **Existing branch** or **No branch**, then pick from a searchable list of local and remote branches, grouped by remote. |
| New branch | A name, and **From** (default: each repo's default branch). Refused when the name already exists, locally or on a remote, with a pointer to Existing branch. |
| Existing branch | Picked from the list. A remote-only branch (`upstream/feat/x`) is checked out as a local branch tracking that remote. |
| No branch | **At** (default: the default branch). Makes a free slot with its worktrees detached there, ready to claim. |
| Release | Renamed **Free** everywhere: the header, the overview's bulk actions beside Remove, and the CLI (`sprig free`, with `release` kept as another name for it). |
| Claiming | The form lets you pick which slot: the lowest free one (as now), any other free one, or a new one. A reused slot still asks keep-data or start-empty and starts from its kept values. |

## Pieces

**Engine**
- `GitClient.Branches(repo)`: local and remote-tracking branches. `AddDetachedWorktree`. Adopting can name the remote to track.
- `CheckOutOptions.NewBranch` refuses a name that exists; `CheckOutOptions.Track` names the remote to adopt from.
- `WorkspaceService.BranchChoices(loaded)`: the branches across an orchestration's repos, saying which repos have each and which is the default.
- `WorkspaceService.MakeFreeSlot(loaded, at, slot)`: a new slot, its worktrees detached at `at`, recorded as free.

**App**
- The form's mode radios, branch pickers (an `AutoCompleteBox` that lists everything when opened), and slot picker.
- **Free** in the header and the overview's bulk actions.

**CLI**
- `sprig free` (also `release`).

## As built (2026-11-10)

All 424 tests pass. The form was checked with a view-model test against a real repo (free slot made, a taken new name refused, an existing branch checked out into a picked new slot) and a headless render of each mode with the branch list open.

- **The list's groups** are shown by order and by the remote's name in front (`origin/…`, `upstream/…`), not by group headings.
- **An existing branch some repos lack** is made in those from their default branch, as before; the list says "only in shop-web" beside such a branch.
- **A free slot made with no branch** has worktrees only. Nothing is installed or started, and ports are leased when a branch is first checked out in it.
- **Not built yet:** the CLI's `--new-branch` and `--no-branch` on `sprig up`. The CLI still adopts a branch that exists, or makes it when it doesn't.
- **Free in bulk** passes over slots that are already free and in-place workspaces.
