# Repo first: settings with defaults, plain names, and one repo made easy: plan

Branch: `experimental`. Written 2026-11-06. Status: agreed (user, 2026-11-06), and all five steps done the same day. Waiting on the user's hand pass. Breaking changes allowed: no migration for definitions or store data.

## Why

Sprig does three jobs:

1. Define a repo so a copy of it can be checked out, set up and made ready in complete isolation.
2. Start every process a copy needs in a sensible order, and let you stop one to debug it yourself.
3. Link several repos and run them together.

The engine does all three. What it shows people doesn't put the first two first. Before running one repo, someone meets Repos entries, trellises, sprouts, imports, exports, needs, offers, auto-binding, `Bind`, `OrElse`, the blocking checklist and the Fill-it dialog. Most of these exist only for the third job, and they reach the first two through one rule: **a repo with an unfilled import can't run.** So shop-web can't run unless something also runs shop-services, even when a URL you already have would do.

This plan gets single repos right first. Orchestration stays possible and keeps working as it does today, but its redesign waits for a later plan.

## Decisions

1. **Plain names** (user, 2026-11-06). A **repo** says what it runs. An **orchestration** (was trellis) runs several repos together. A **workspace** (was sprout) is one running copy of a repo or an orchestration: worktrees or the folder itself, with its ports, values and runners.
2. **Several definitions per folder stay** (user, 2026-11-06). A repo ID is in repo or in sprig for good, as today.
3. **A repo always runs on its own.** What it takes from outside is a **setting**, with a default when it can have one:

   ```csharp
   var catalog = repo.Setting<Url>("catalog-url", "http://localhost:5100");
   var smtp    = repo.Setting<Url>("mail-smtp", repo.Mailpit("mail").SmtpUrl); // its own Mailpit starts only when it's used
   var key     = repo.Setting<Secret>("payments-key");                        // no default: asked for when a workspace is made
   ```

4. **Where a setting's value comes from**, the first that has one:
   1. **The workspace's own value**, set when the workspace is made or later (`sprig set`). It's the most specific choice, made for this copy, so it wins. This lets you run an orchestration but point one repo at staging.
   2. **The orchestration:** a binding, or the one repo in it that exports the name (today's behaviour, kept until the orchestration redesign).
   3. **The repo's default.**
   4. **Nothing:** asked for when the workspace is made, and kept on the workspace. A setting with no value never blocks a repo from being chosen; it's a question, not an error.

   This replaces my first suggestion, which put the orchestration's link above the workspace's value.
5. **The default workspace is the folder itself.** `sprig up` in a repo's folder runs it in place as `local`. Naming a workspace (`sprig up -w feat-x`) makes worktrees. Naming a workspace means asking for isolation.
6. **Orchestration is frozen, not removed.** It's renamed and keeps working, with today's binding rules, so the testbed's `storefront` still runs. Linking, `Repo(path)` against `UseRepo(id)`, auto-binding and whether an orchestration should be code at all are for a later plan.

## Steps

Each step leaves the app building and the tests passing. Sizes: S is under a day, M is one to two days, L is more.

### 1. The new names (L) ✅ done 2026-11-06

Built as planned, and all 317 tests pass. The model schema is 10. `--sprout` existed only on `up` and `plan`, which now take `-w/--workspace`. `down`, `rm`, `env`, `start`, `stop` and `logs` keep their positional `<id> <workspace>`. The progress heading says "Starting", and the new-workspace form's button says "Run it". The dev store's data under the old folder names was left in place rather than deleted, since a workspace there may still own containers.

This step is mechanical but touches a lot: about 750 uses across 53 files, plus tests, the testbed and the docs. It comes first so new code isn't written under names that are about to go.

- **SDK:** `Trellis.Describe` becomes `Orchestration.Describe`, and `TrellisModel` becomes `OrchestrationModel`. The `{sprout.name}` template becomes `{workspace.name}`. Files are `<name>.orchestration.cs`. Model schema 9.
- **Engine:** `Sprouts/` becomes `Workspaces/` (`WorkspaceService`, `WorkspaceRecord`, `WorkspaceStore`, `WorkspaceOptions`), and `Trellises/` becomes `Orchestrations/`. The store folders are renamed to match. Compose projects become `sprig-<id>-<workspace>`, and worktree folders `<id>--<workspace>`.
- **CLI:**
  - `sprig orchestration add|ls|rm` and `sprig workspaces`.
  - `--workspace`/`-w` replaces `--sprout`.
  - The argument of `up`, `plan`, `start` and the rest is a repo ID or an orchestration ID.
- **App:** nav labels, page titles, dialogs and messages. The nav becomes **Repos**, **Workspaces**, **Orchestrations**, in that order.
- **No migration:** dev store data under the old folders is deleted. The changelog note that will go with the merge to `main` says so.
- **Done when:** nothing a user sees says trellis or sprout, the testbed's orchestrations plan and run under the new names, and every test passes.

### 2. Settings with defaults (M) ✅ done 2026-11-06

Built as planned. All tests pass apart from one host Ctrl+C timing test, which failed once under a full run and passed 20 of 20 on its own. The model schema is 11. `repo.Setting<T>(name, default?)` is one method with an optional default, because with a separate no-default overload a string default was taken as the caller's file path. A value set on the workspace counts as "this workspace" in the plan's sources. `--set name=value` came forward from step 3 onto `up` and `plan`. An interactive `up` prompts for each value it's missing (secrets masked), then goes again. `PlanInput.Alone` is gone; `LoadedOrchestration.Alone` stays, since the Workspaces page uses it. `DefinitionEditor.AddImport` became `AddSetting`. Retired as planned: `NeedFixes`, the Fill-it dialog and the checklist's needs step, so the checklist now has four steps.

- **SDK:**
  - `repo.Setting<T>(name, default?)` replaces `repo.Import<T>(...)`.
  - The default can be a literal or anything the repo declares, such as its own Mailpit.
  - `Import` and `OrElse` stay only as compile errors that point at `Setting`.
  - `ImportModel` becomes `SettingModel(Name, Type, Default?, Location)`.
  - `Export` is unchanged.
- **Engine:**
  - `PlanInput` gains the workspace's own values. The planner fills settings in the order in decision 4.
  - Infra that only a default uses is left out whenever the setting is filled some other way, which extends today's `UnneededFallbackInfra`.
  - A setting with no value isn't a plan problem. It's listed as **to ask**, and `up` refuses only if it's still unanswered when it gets there.
  - `PlanInput.Alone` and the "can't sprout alone" message go.
- **Workspace values:**
  - Kept in the workspace record, `Secret` ones included, as the store keeps other per-user data.
  - Changing one rewrites the env files that use it and names the apps that need a restart.
- **Retired:** `NeedFixes`, the Fill-it dialog (`FillNeedsViewModel`/`FillNeedsView`) and the checklist's needs items. Nothing is unfilled any more, so there's nothing for them to fix.
- **Testbed:**
  - shop-web's `catalog-url` and `orders-url` default to the APIs' own ports from running them by hand (M2), so shop-web runs alone against an in-place shop-services.
  - shop-services' `mail-smtp` uses its own Mailpit as its default.
- **Done when:** shop-web runs on its own pointed at an in-place shop-services, with no orchestration. `storefront` still plans with the same values as before. Tests cover each step of decision 4, a setting asked for at `up`, and default-only infra being left out.

### 3. One repo from a terminal (M) ✅ done 2026-11-06

Built as planned, and all 318 tests pass. Checked on a fresh testbed with a throwaway store, from shop-web's folder:
- `init` added the folder's own sprig.cs.
- `up --trust` ran it in place as `local` with its default urls.
- `set catalog-url=…` rewrote `.env.local` and said to restart `web`.
- `up -w feat-x --set orders-url=…` made a worktree workspace.
- `settings` from inside that worktree showed feat-x's values without being told which workspace.
- `rm feat-x -y` and `rm -y` cleaned up.

Beyond the plan:
- **Inside a worktree:** the commands work out the workspace from the worktree they're run in.
- **Positional commands:** `down`, `rm`, `start`, `stop`, `logs` and `env` take `[id] [workspace]`. A first word that isn't an ID means the folder's repo, and a word that isn't one of its workspaces is an app. So `sprig logs web` and `sprig stop catalog-api` work from the repo's folder.
- **New options commands:** `set`, `unset` and `settings` name the ID and workspace with `--id` and `-w`.
- **`set` and `unset` on a workspace that's up** bring it up again so its files change, and name the apps to restart.
- **`init` on a listed folder** says so, and keeps the entry it has.
- **`init --in-repo`** writes the draft, approved, since sprig wrote it.

- **`sprig init`** in a folder writes a definition from what the folder has, reusing `Scaffold`: a `.csproj` becomes `DotNet(...)`, a `package.json` becomes `Node(...)`, and a compose file becomes `ComposeFile(...)`.
  - It adds a Repos entry with the folder's name as its ID.
  - It keeps the definition in sprig, so nothing in the repo changes and it works at once. `--in-repo` writes `sprig.cs` into the folder instead.
- **`sprig up`** with no argument runs the folder's repo. If the folder has more than one ID, it lists them and asks for one. If it has none, it offers `init`.
  - Without `-w`, it runs in place as `local`. `-w feat-x` makes worktrees, and `--from` and `--random-ports` work as they do today.
  - It asks for any setting still without a value. `--set name=value` answers ahead, for scripts.
- **`sprig set name=value [-w x]`**, **`sprig unset name`** and **`sprig settings`**, which lists each setting with its value and where it came from (this workspace, the orchestration, the default).
- `plan`, `start`, `stop`, `ps`, `logs` and `env` also default to the folder's repo and `local`.
- **Done when:** in a fresh testbed copy of shop-web, `sprig init` then `sprig up` brings the app up in place, asking nothing, because every setting has a default. `sprig up -w feat-x --set catalog-url=...` makes an isolated copy pointed elsewhere.

### 4. One repo in the app (L) ✅ done 2026-11-06, with one change

All 320 tests pass. Checked with view-model tests and headless renders (in `captures/`, `*-repo-first-*.png`).

- **Changed from the plan:** a repo's workspaces are listed on its page (state, in place or which branch, **Open**), but their runner rows stay on the Workspaces page, one click away. Copying the runner rows, log pane and host polling onto the Repos page would have meant two copies of the most involved page. Whether that's the right split is for the hand pass.
- **The new-workspace form** starts in place the first time, as `sprig up` does. It lists the chosen repo's or orchestration's settings once their definitions are approved. A setting that nothing else gives a value must be filled in; the others say what they'll get when left empty ("Empty: its own mail"). Settings it couldn't show before approval are added when **Run it** is pressed, which then asks for any it still needs.
- **The workspace header** has **Settings** (left of ⋯). It opens a dialog with each setting, the workspace's own value, and what applies when that's empty. Saving keeps the values. If the workspace is up, it comes up again and names the apps to restart.
- **Up with a setting still to ask** (a branch can add one) fails in the progress window and points at Settings.
- **The Repos page's button** is **New workspace**.

The Repos page becomes where a repo is run, not just described. The existing layout rules apply: focused tasks open in a large dialog, a ⋯ menu is always the rightmost control, nav rows stay plain, and page headings have no subtext.

- **A selected repo shows, in order:**
  - its workspaces, each with its runner rows (start, stop, restart, log) and **New workspace**
  - its settings, each with its value and where it came from
  - what it runs and its infra
  - its definition (open and edit)
- **The New workspace dialog** asks:
  - in place, or its own worktrees (branch, start from)
  - same ports each time, or fresh ones
  - a value for each setting with no default, with the others shown and editable
- **A workspace's settings** can be edited later from the workspace, in a dialog that says which apps restart.
- **The Workspaces page** lists every workspace, of repos and orchestrations, for when you know the copy but not where it came from.
- **Adding a repo** picks the folder, then offers what `init` would write, kept in sprig or written to the folder.
- **Checked with:** view-model tests and headless renders. The hand pass waits until the structure is done.
- **Done when:** a repo can be added, started in place, given a worktree workspace with one setting changed, and have one app stopped and restarted, all from the Repos page.

### 5. Orchestrations, frozen (S) ✅ done 2026-11-06

On a fresh testbed with a throwaway store:
- `sprig plan storefront` and `sprig plan catalog` showed no problems. `mail-smtp` came from the orchestration and `catalog-url` from shop-services.
- `sprig up storefront -w s5 -d` had all five apps ready in 21 s, with only the orchestration's Mailpit running (shop-services' own was left out).
- `sprig stop` and then `start` on catalog-api brought it back healthy.
- `rm --delete-branch` left no containers or volumes.

The app's Orchestrations page planned storefront with no problems in the headless render. Its button now says **New workspace**, as the Repos page's does. The changelog's Unreleased section uses the new names and describes settings in place of Fill-it and `OrElse`.

- The Orchestrations page keeps its Overview, Values, Ports and Code tabs under the new names. It loses the needs checklist (step 2), and each repo card shows its settings with where each came from.
- No new orchestration features until the later plan.
- **Done when:** `storefront` and `catalog` plan, start and stop from the app and the CLI as they did before step 1.

### Hand pass, first round (2026-11-06)

Agreed with the user, and built the same day:
- **`sprig up` starts the apps**, in the background, and waits until they're ready. `--run` follows their logs, and `--no-apps` stops at the infra. `-d` is gone.
- **A workspace's name is enough from anywhere.** Outside every repo, a bare name (`sprig rm feat-x`, `sprig settings -w feat-x`) finds the one workspace with it, and asks for the ID when several have it. Inside a repo, words mean that repo's workspaces as before.
- **`settings` stays, and `sprig info` shows a workspace whole:** state, folder, apps with state and url, infra ports and settings.
- **The app reloads the page shown when its window comes back to the front**, unless a dialog or an edit is open. Live file watching was turned down as more than this needs.
- **Fixed:** `shop-web--feat-x` counts as the feat-x workspace, not only the `shop-web` worktree inside it.
- **Helpers:** `scripts/dev-sprig.ps1` and `dev-sprig.cmd` put this checkout's build first on one terminal's PATH, with `-Store` for an empty store.

Still open: a one-repo workspace could put its worktree straight in `shop-web--feat-x`, without the `shop-web` folder inside.

## Order

1 first, so nothing new is written under the old names. 2 before 3 and 4, since a repo that runs alone needs settings. 3 before 4, so the app's flow copies a CLI flow that has already been checked. 5 can happen at any point after 2.

## For the later orchestration plan

- Explicit links (`orch.Link(web, "catalog-url", services.Export("catalog-url"))`) in place of auto-binding by name.
- One way to name a repo in an orchestration, by ID, rather than both `Repo(path)` and `UseRepo(id)`.
- Whether an orchestration (repos, links, shared infra, groups) should be data edited in the app rather than C#, which would also mean fewer approvals.
- Values for a repo as a whole, applied to every new workspace of it, if values per workspace prove repetitive.
