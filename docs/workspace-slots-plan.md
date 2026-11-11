# Workspace slots: numbered worktrees, the branch as the label: plan

Branch: `experimental`. Written 2026-11-09. Status: agreed (user, 2026-11-09), and steps 1 to 4 built the same day. Waiting on the user's hand pass. Breaking changes allowed: no migration for store data.

## Why

A workspace has a name of its own (`feat-x`) as well as a branch. The name only exists inside sprig. In a git graph, `git worktree list` or an IDE, all you see is branch names and worktree folders, so the name is a second label that nothing else can read.

Worktrees are also thrown away with their workspace. A new task makes new folders, so IDE projects, build caches, `node_modules` and tool settings start again every time.

## Decisions

1. **A workspace is a numbered slot** (user, 2026-11-09). Its folder is `<id>--<n>`, where n is the lowest number not already used, starting at 1. The folder keeps its name for as long as the slot exists, so it can be reused.
   - **An orchestration's slot is one folder holding its repos** (user, 2026-11-09): `storefront--1/shop-web`, `storefront--1/shop-services`. They share the slot's number, ports and compose projects, as they do today.
   - **A repo on its own is the slot folder itself:** `shop-web--1` is the worktree, with no `shop-web` folder inside. This settles the question left open in the repo-first plan.
2. **The branch is the label** (user, 2026-11-09). There's no workspace name. A slot shows the branch its worktrees are on (`shop-web--1 · feat-x`), so sprig, git and other tools all read the same thing. Every repo in an orchestration's slot uses the same branch name.
3. **A detached HEAD means the slot is free, and a free slot is a warm cache** (user, 2026-11-09). Everything a slot built up stays ready for the next branch, so nothing has to be installed again. Releasing a slot:
   - stops its apps
   - takes its infra down, but keeps the volumes (user, 2026-11-09)
   - switches every worktree to a detached HEAD at the commit it was on
   - keeps the branch, so no commits are lost
   - keeps the slot's values, its build output, `node_modules` and the files sprig wrote
   - refuses while a worktree has uncommitted changes, naming it
4. **Checking a slot out decides what carries over** (user, 2026-11-09). A new workspace takes the lowest free slot, or makes the next number if none is free. When it reuses a slot:
   - **Data:** keep the data that's there, or start empty. Starting empty removes the slot's volumes first.
   - **Values:** the form starts with the values the slot kept, and each one can be changed or cleared back to its default. On the CLI, `--set` overrides a kept value and `--unset` clears one.
5. **A slot keeps its ports for its whole life** (user, 2026-11-09, already built). The port choice and `--random-ports` are gone.
6. **Removing a slot is separate from releasing it.** Remove deletes the worktrees, the folder, the containers and the volumes, and frees the ports. As today, it can also delete the branch, and it refuses unpushed commits unless forced.
7. **The checkout itself waits for its own design** (user, 2026-11-09). The app no longer offers "in place" (already built). The CLI's `sprig up` without a branch still runs in place as `local` until a later plan. That plan might give each repo a fixed `main` slot that can't be removed.

## How it looks

**CLI.** A workspace is named by its branch or its slot:
- `sprig up -b feat-x`: checks out feat-x in the lowest free slot. The branch is made from `--from`, or from each repo's default branch, when it doesn't exist yet. `--slot 2` asks for a particular slot.
- `sprig release feat-x`: frees the slot.
- `sprig rm 2`: deletes the slot.
- `sprig logs feat-x web`, `sprig info 2` and `sprig settings -b feat-x` all accept either form. Inside a slot's folder, commands mean that slot, as they do today.
- `-w/--workspace` is gone.

**App.** The Workspaces list has a row per slot: `shop-web--1 · feat-x · up`, `storefront--2 · free`.
- **The New workspace form** asks for the repo or orchestration, a branch, where to start from, and settings. It says which slot it will use. When that slot is a reused one, it adds keep-data and start-empty options, and fills the settings with the values the slot kept.
- **A slot in use** has **Release** in its header. **Remove…** is in its ⋯ menu.
- **A free slot** has **Check out a branch** and **Remove…**.

**Git.** `git worktree list` shows `…/shop-web--1  [feat-x]`, and a free slot shows `(detached HEAD)`. Asking for a branch that's already checked out in another slot is refused, naming that slot.

## Steps

Each step leaves the app building and the tests passing. Sizes: S is under a day, M is one to two days, L is more.

1. **Slots in the engine (L).**
   - `WorkspaceRecord` is keyed by `<id>--<n>`, carries the slot number, and has a branch only while in use. It gains a `Released` state.
   - Compose projects become `sprig-<id>-<n>`, and port leases are keyed by slot.
   - A one-repo slot puts the worktree in the slot folder itself.
   - Release, check out again (keeping or wiping the data) and remove all work as described above.
   - Data under the old keys isn't carried over.
2. **CLI (M).** `-b`, `--slot`, `release`, and naming a workspace by its branch or slot number everywhere one is named today. `Here` maps a folder to its slot.
3. **App (M).** The slot rows, the form's slot line and data choice, **Release**, and a free slot's actions.
4. **Testbed and docs (S).** The testbed README, the changelog, and the hand-pass checklists' wording.

## As built (2026-11-09)

All 333 tests pass. Checked by hand with the CLI on a throwaway testbed:
- `up -b feat-x` and `up -b feature/y` made `shop-web--1` and `shop-web--2`.
- `release feat-x` detached slot 1.
- `up -b feat-z` reused slot 1 on a new branch.
- `git worktree list` showed `shop-web--1 [feat-z]`.
- `settings` run inside `shop-web--2` knew it was feature/y.
- `up -b main` was refused, because main is checked out in the repo's own folder.

The app's form, **Release**, the free slot's actions and the slot rows were checked with view-model tests and a headless render.

- **Kept from before:** a workspace's internal name. It's the slot number, so store keys (`storefront/2`), compose projects (`sprig-storefront-2-…`), port leases and folders (`storefront--2`) all follow from it unchanged. `WorkspaceRecord.SlotName` and `Label` are what people see.
- **Not in the plan:** the branch a slot shows is read from git when it comes up. A `git switch` made inside a slot relabels it, rather than sprig switching it back.
- **Not yet done:**
  - The engine still accepts the old port-policy field from existing records.
  - In-place `local` workspaces are unchanged.
- **Hand pass:** `docs/artefacts/repo-writing-checklist.html` covers slots: V5 to V8, and I7 to I9.

## After the user's first use (2026-11-09)

Feedback from trying slots on a real repo, built the same day:
- **Logs go with the branch.** A slot keeps its key from one branch to the next, so its logs used to carry over. Release and remove delete them, and a check-out into a new or free slot clears any left behind. The app's log pane treats a new branch in the same slot as a different workspace, so lines it already read go too.
- **A broken slot asks.** A slot is broken when, where its worktrees go, there are folders that aren't worktrees of their repos. Only a new or free slot counts. The form asks whether to use the next slot or fix this one (BROKEN SLOT, starting on the choice that deletes nothing). `sprig up -b` asks in a terminal, takes `--fix-slot`, or refuses without a terminal and names both ways. Fixing deletes the folders, removing any link inside them without following it, then prunes git's worktree list. A free slot whose folder was deleted by hand is pruned and made again without asking, since there's nothing to lose.
- **Groups in a repo, and a launch-profile Start.** `repo.Group(name, apps…)` (model schema 13). Repos' groups of the same name join in an orchestration, and the orchestration's own group of that name replaces them. Start in the app gained a ▾ to pick every app or one group, remembered per repo or orchestration in `ui.json`. `sprig up --group` and `sprig start --group` do the same.
- **Groups grow what's running** (user, 2026-11-09). Starting group B after group A leaves A's apps going and starts only the rest, so you can build up as you go.
- **Restart what's running** (user, 2026-11-09). **Restart** in the app and `sprig restart [apps] [--group g] [--jobs]` restart every running app, each built again and in order. Stopped apps stay stopped and aren't waited for, since one may be running in an IDE on its port. Jobs are re-run only when asked (the ⋯ menu's **Restart running, jobs first**, or `--jobs`).
- **A group starts only its infra** (built 2026-11-10): `WorkspaceService.InfraFor` follows the plan's topology from the group's apps (with what they depend on) to the containers they take values from or wait for, and on through infra that uses other infra. A value from another app doesn't count. Up starts only those services; the other projects' files are still written. Starting more on a workspace that's up asks docker what's running (`MissingInfra`) and starts the rest first (`StartInfra`). Still starting everything: Bring up, Bring up again, and the re-up after a value changes.

## For later

- Each repo's checkout as a fixed `main` slot (decision 7): built 2026-11-10, see docs/main-checkout-plan.md.
- A slot pool size: making slots ahead of time, or capping how many there are. Dropped for now (user, 2026-11-11): free slots already act as the warm cache, and making slots ahead only speeds up the first check-out into each. Revisit if disk use or first check-outs become a problem.
