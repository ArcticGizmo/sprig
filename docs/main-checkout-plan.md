# The main checkout as a fixed `main` row: plan

Branch: `experimental`. Written 2026-11-10. Status: agreed (user, 2026-11-10). Breaking changes allowed: the in-place `local` workspace goes, with no migration. Since 2026-11-13, main writes no values and asks for none: see `main-runner-plan.md`.

## Why

Slots run branches in worktrees of their own, but the repo's own checkout (where you work most of the time) had no place in the app, and only the CLI's `sprig up` without `-b` could run it, as a workspace named `local`. The user wants to use the runner on the main checkout too.

One checkout can have more than one definition: a repo's own `sprig.cs` and an in-sprig copy of it, or an orchestration that includes the repo. Each is a different ID with its own `main`, but they all run in the same folder.

## Decisions (user, 2026-11-10)

1. **Every repo and orchestration has a `main` row**, first in its group, always there. It can't be removed or freed, and its values can't be set: it runs with the definitions' own defaults. Its actions are Start ▾, Stop, Restart, Take down, Bring up and the logs. A setting with no default stops it starting, naming the setting: give it a default in the definition, or run in a slot.
2. **It's named after its ID**, the way slots are named `<id>--<n>`: `shop-web` beside `shop-web--1`. It shows the branch the checkout was on when it last came up, since the main checkout isn't always on `main`.
3. **One definition at a time runs in a checkout.** A `main` holds its folders while it's up. Starting another definition's `main` in any of the same folders asks first: "repo-a is up in `<folder>`. Take it down and start repo-b here?" Taking over stops the other's apps and takes its infra down, keeping its volumes. A `main` that's down holds nothing, so nothing has to be removed to switch.
4. **A row that's been taken over says so**: "repo-a is up here".
5. **Data stays per definition.** Each definition's `main` has its own compose project, so two definitions of one repo don't share a database.
6. **Not a checkout mode.** The new-workspace form stays about slots: nothing is checked out for `main`, and none of the form's choices apply to it.

## Pieces

**Engine**
- `WorkspaceRecord.MainName` (`main`) and `IsMain`. A `main` is in place, its slot name is its ID, and `CheckoutBranch` records the branch the checkout was on when it came up.
- `WorkspaceService.MainOf(id, path)`: the stored `main`, or one that has never come up (not saved) for showing.
- `Up` always runs `main` in place, refuses values for it, and says what to do about a setting with no default rather than asking.
- `WorkspaceService.HolderOf(loaded, workspace)`: another in-place workspace that's up in the same folders. `Up` refuses while there is one (the exception carries it), unless told to take over, which takes that one down first.
- `SetValues` and `Remove` refuse `main`.

**CLI**
- `sprig up` with no `-b` means `main`. It asks before taking over in a terminal, and `--take-over` answers yes; without a terminal it refuses and names `--take-over`.
- Every command that defaulted to `local` defaults to `main`.

**App**
- The Workspaces list and overview show each repo's and orchestration's `main` first in its group, made on first start.
- A `main` has no Free, Settings or Remove. Starting one that another definition holds opens a dialog to take over.
- A `main` that another definition holds says which.

## As built (2026-11-10)

- **An unmade main** is a record `MainOf` returns but never saves: `CreatedAt` is `default`. The app lists it, shows "never" for its last up, and leaves it out of Take down and Remove in bulk. Bringing it up saves it.
- **Who holds a checkout** is any other in-place workspace that's up or starting with a repo in the same folder. `HolderOf(record)` works from the folders a main ran in last time, so the list can say "repo-a is up in this checkout" without reading definitions. A main that has never come up has no folders yet, so it says nothing until Start asks.
- **The app asks in a dialog** ("Take over from repo-a?") when Start or Bring up finds a holder. Taking over is a step in the progress window ("Take over from repo-a"), followed by the other's own Stop steps.
- **The in-place `local`** still works for a record that has it, but nothing makes one now. The headless render's demo still uses it, because it needs a workspace with values set.
- **Tests:** three engine tests (main itself, a setting with no default, taking over) and the app's workspace test, which brings `main` up and takes it over from a second orchestration.

## After the user's first use (2026-11-10)

- **The overview offered to remove main.** Ticking a main showed the bulk Remove…, whose dialog then passed over it without saying. Remove and Free now show only when something ticked can be removed or freed, and the dialog says a ticked main stays.
- **Main goes with its ID** (user, 2026-11-10). Main is only always there while its repo or orchestration is. Removing either (on its page, `sprig repo rm`, `sprig orchestration rm`) removes its main first with `RemoveMain`: its containers and data, and the values it wrote put back. If that fails, the repo or orchestration stays listed and the dialog says why.
- **Renaming a repo** is refused while its main is up, and otherwise drops the old ID's main, data included, which the rename dialog says first. Other workspaces still block a rename.
- **A main whose ID has gone** some other way gets Remove… on its page, so nothing is left that can't be cleared.

## Emptying data (2026-11-11)

Built for every workspace, main included (user, 2026-11-11: "empty and bring back"). **Empty data…** in the ⋯ menu, with a dialog, and `sprig empty-data [-y]`. `WorkspaceService.EmptyData` stops the apps and takes the compose projects down with their volumes, leaving the workspace down. The app and the CLI then bring it back up if it was up, and start again the apps that were running, with the jobs they depend on run again, since a finished migration ran against the old data. Checked on the testbed: Postgres's volume was made again, migrate ran again, and only the app that had been running came back.
