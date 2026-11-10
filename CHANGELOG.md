# Changelog

All notable changes to sprig are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added
- **A group starts only the infra it uses** — Start ▾ with a group, `sprig up --group` and `sprig start --group` bring up just the containers its apps (and the jobs and apps they depend on) take values from or wait for, and what those use in turn. The rest is written but left off, and its step in the progress window says so. Starting another group, or one app, on a workspace that's already up starts whatever it adds first, and leaves what's running alone. Every app, or Bring up, still starts all of it.
- **Secrets follow you into worktrees** — `repo.CopyIntoWorktrees("backend/src/.env")` copies a file or folder git doesn't have from the repo's own checkout into each slot's worktree, so a build that needs it works there too. A copy the worktree already has is kept, so a change made there stays, and the progress says so. It has to be something the repo ignores and doesn't track, which is checked before any worktree is made. Links inside a copied folder are left behind.
- **Main, the checkout you already have** — every repo and orchestration lists a `main` workspace first, before it has ever run: the repos where they're checked out, with the branch they're on. It runs with the definitions' own values, so there's nothing to set, free or remove; a setting with no default stops it and says to give it one. `sprig up` without `-b` runs it, replacing the in-place `local`. Two definitions of one checkout (a repo's own `sprig.cs` and a copy kept in sprig, say) take turns: starting one while the other is up asks to take over, which takes the other down with its data kept. `sprig up --take-over` answers for you, and the one taken over says who has the checkout now.
- **One repo, two commands** — in a repo's folder, `sprig init` makes it runnable (its own `sprig.cs`, or a draft from its projects kept in sprig, or written in with `--in-repo`), and `sprig up` runs it where it's checked out. `sprig up -b feat-x` gives a branch worktrees of its own. No orchestration, no ID to remember: the commands work out the repo from the folder, and from inside a worktree, which workspace you mean. `sprig logs web` and `sprig stop catalog-api` do what they say from there too.
- **Settings, with defaults** — `repo.Setting<Url>("catalog-url", "http://localhost:5100")` is what a repo takes from outside, and what it uses when nothing else says otherwise, so every repo runs on its own. A value set on the workspace wins (`sprig up --set catalog-url=…`, `sprig set`, or the workspace's **Settings** dialog), then the orchestration's, then the default. A setting with no default is asked for when the workspace is made, rather than refusing to run. A default can be the repo's own infra (`repo.Mailpit("mail").SmtpUrl`), which is left out whenever something else gives the value. `sprig settings` lists each one and where it came from; changing one on a running workspace rewrites its files and names the apps to restart.
- **A repo's page lists its workspaces** — each with its state and an **Open** that goes straight to its apps.
- **A folder in the way is caught at Create** — a slot folder that's already there and isn't a worktree of the repo, or a branch checked out somewhere else, is said in the new-workspace form with the form still open, instead of partway through in the progress window. `sprig up -b` checks the same before it makes anything, so it no longer leaves the first repo's worktree behind when the second can't have one.
- **Rename a repo kept in sprig** — from the ⋯ menu on its page. Its definition and approval come along. It won't while an orchestration uses it or it has workspaces of its own, since those are all filed under the old name, and it says which to clear first rather than leaving them looking for a repo that isn't there.
- **`sprig info`** — one workspace, all of it: up or down, where it lives, each app's state and url, its infra's ports, and every setting with where it came from.
- **A branch is enough** — `sprig rm feat-x`, `sprig logs feat-x web` and `sprig settings -b feat-x` work from any folder when only one repo or orchestration has `feat-x` checked out. When several do, sprig names them and asks for the ID.
- **The app keeps up with the terminal** — switch back to the window after `sprig init` or `sprig up` and the page shows what changed. A dialog that's open, or an edit in progress, is left alone.
- **Ctrl+C ends a dev build properly** — run from a terminal with `dotnet run --project src/Sprig.App`, Ctrl+C stops every app, then the host, then sprig, even while it's hiding in the tray.
- **Remove asks in a dialog** — on the Repos and Orchestrations pages, as it already did for workspaces, rather than a banner that pushed the page down. An orchestration kept in its repo asks too now, though only its listing goes.
- **Quick links** — `app.Link("Swagger", "/swagger")` and `orchestration.Link("Mail inbox", mail.UrlOf("ui"))` name the pages you actually start from, at each workspace's own ports. App links lead the details panel; orchestration links live under the workspace's **Links** button. Click to open, middle-click for a new window. Containers grew `UrlOf` so their web UIs can be linked to.
- **Groups in a repo, and Start picks one** — `repo.Group("client", web, admin)` names a slice of a repo's apps to start on its own, so looking at a front end doesn't mean running every API behind it. Whatever a group's apps wait for still starts first, in order. In an orchestration, repos' groups of the same name start together, and the orchestration's own group of that name wins. The workspace's **Start** grew a ▾ that picks every app or one group, like a launch profile, and remembers the pick per repo or orchestration. `sprig up --group client` and `sprig start --group client` do the same from a terminal. Starting a group grows what's running rather than starting over: apps already going are left alone, and only the rest of the group starts.
- **Restart what's running** — after a change in the worktree, **Restart** beside **Stop apps** (or `sprig restart`) builds every running app again and brings it back after what it depends on. Stopped apps stay stopped and aren't waited for, so the one you're running from your IDE keeps its port. Jobs such as migrations are left finished unless you ask: **Restart running, jobs first** in the ⋯ menu, or `sprig restart --jobs`. `sprig restart --group apis` or `sprig restart web` restarts only those, if they're running.
- **A broken slot asks what to do** — when the slot a branch would take has folders in its way that aren't worktrees (a slot whose record went, but whose folder stayed), the new-workspace form asks whether to use the next slot or fix this one. Fixing deletes what's in the way and makes the slot again; a link inside it, such as a pnpm junction, is removed, never followed. `sprig up -b` asks the same in a terminal, or takes `--fix-slot`, or `--slot <n>` for the next. A worktree folder deleted by hand no longer stops its slot from being made again.
- **Logs you can work with** — drag or Shift+click lines and Ctrl+C them, stack traces included. Right-click a line to see what every app was doing at that moment, or the reverse. Timestamps and wrapping can be switched off, and stay off.

- **sprig lives in the tray** — close the window while apps are running and sprig tucks itself into the notification area instead of leaving them orphaned. From there: open it again, or quit, which stops every app first, since apps left running with no sprig to show them are orphans nobody asked for. Close it with nothing running and the background host goes too, rather than loitering for ten minutes holding files hostage. Launching sprig again brings back the one you have instead of starting a second.

- **Orchestrations and definitions kept in sprig** — try sprig on repos without committing a thing. **+ Add** on the Orchestrations page makes an orchestration from a few repo folders, and any repo without a `sprig.cs` gets one kept in sprig, drafted from its `.csproj` and `package.json` files. Edit any of them in the app, with completion and compile errors; saving approves what you saved and checks it again.
- **A Repos page** — each repo is described once: its folder, what it runs, offers and takes, the orchestrations that use it, and where its definition lives: **in repo** or **in sprig**, for good, so nothing an orchestration runs changes under it. **Copy into sprig** gives you a new ID to try changes on without touching the repo; **Write to the repo** puts an in-sprig definition into the folder as `sprig.cs` (uncommitted, and never over one that's there) under a new ID of its own. Orchestrations use repos by ID with `orchestration.UseRepo("shop-web")` or by folder with `orchestration.Repo(path)`, and **Add to Repos** turns the second into the first.
- **+ Add a repo asks which kind first** — like Orchestrations: **It has a sprig.cs** uses the one in the repo, and **Write one in sprig** keeps one in sprig, starting from a copy of the folder's `sprig.cs` if it has one or a draft of its projects if not, and opens it for editing. The Repos and Orchestrations pages lost the line under their headings and now share a list width, so flicking between them no longer makes the page jump.
- **`sprig repo`** — `add`, `copy`, `ls` and `rm` do from a terminal what the Repos page does, so a script can set up the IDs an orchestration uses. `add --in-sprig` keeps the definition in sprig. Adding a folder that's already listed shrugs and moves on, so scripts can run twice without leaving a trail of `shop-web-2`s. `rm` won't take an ID an orchestration uses unless you `--force` it. The testbed's new `catalog.orchestration.cs` uses repos by ID this way.
- **An orchestration page that gets to the point** — an orchestration opens on what it runs and whether it can run, with **New workspace** right there. Its picture draws each repo, what the orchestration adds, and a wire for every value that crosses between them; click an app for its values, or a group to see its slice. The full plan moved to **Values**, **Ports** and **Code** tabs, where it can be as long as it likes.
- **A checklist while something's in the way** — until an orchestration can make a workspace, its Overview lists the steps to the first one and the button that clears each, so "can't plan" comes with directions.
- **The editor acts like an IDE** — completion pops up as you start a word (your variables included) and narrows as you type, with each item's docs beside the list; hover anything for its signature and doc comment; errors and warnings get red and amber squiggles; Alt+Shift+F formats the file. Typing `(` or `.` accepts the highlighted item, so `catalog.Ht(` becomes `catalog.Http(` without reaching for Enter.
- **Orchestrations have IDs** — workspaces are keyed by the ID an orchestration is registered under, not the name it gives itself, so two orchestrations that both call themselves `storefront` no longer share workspaces, ports and containers like a pair of confused twins. `sprig orchestration add --id` picks one. Repos and orchestrations share one set of IDs, since either can have workspaces.

### Changed
- **Every value goes in a file the app reads** — sprig no longer sets anything on an app's process, so a plain `dotnet run`, `pnpm dev`, your IDE or a tool that isn't sprig, started in the same worktree, gets exactly what sprig's run gets. Say which file each value goes in: `.EnvFile(".env.local", …)`, `.AppSettings("appsettings.Local.json", …)` or `.LaunchSettings(…)`, on apps and jobs alike. `.Env(...)`, `Http(urlEnv:, portEnv:)` and `.Output(...)` are gone, and code that still uses them stops compiling at that line. A Run command can't carry a value any more (`--port {web.Port}`), since typing it yourself wouldn't fill one in. .NET apps run as a plain `dotnet run`, launch profile included, and sprig's profile starts from the env of the one it displaces, so `ASPNETCORE_ENVIRONMENT` survives. `sprig env` and **Copy env** retired, having nothing left to copy. As a bonus, a gitignored file a repo shares between apps, such as `backend/src/.env`, now follows each worktree from the main checkout, because sprig writes into it.
- **Workspaces are numbered slots, labelled by their branch** — `sprig up -b feat-x` checks feat-x out in a slot of worktrees, `shop-web--1` (or `storefront--1/shop-web` for an orchestration), and the branch is the only name it has, so git, your IDE and sprig all call it the same thing. `sprig release feat-x` frees the slot: its worktrees go to a detached HEAD, the branch and its commits stay, and so do the slot's folders, installed packages, ports, values and data, ready for the next branch to skip the reinstall. Checking a branch out reuses the lowest free slot, and the form (or `--empty-data` and `--unset`) decides whether the old data and values come along. Commands name a workspace by its branch, its slot number or its folder. `-w` is gone, along with names you had to remember and git never saw. A repo on its own now gets its worktree as the slot's folder itself, with no second `shop-web` folder inside.
- **Plain names** — a trellis is an **orchestration** and a sprout is a **workspace**, in the app, the CLI (`sprig orchestration`, `sprig workspaces`) and the SDK (`Orchestration.Describe`, `{workspace.name}`, `*.orchestration.cs`). The garden was lovely; nobody could find anything in it. Data kept under the old names isn't carried over.
- **Imports are settings now** — `repo.Import<T>(…).OrElse(…)` is `repo.Setting<T>(name, default)`, and code that still says `Import` stops compiling at that line with directions. **Fill it…** and the checklist's "What the repos need" step went with it: a setting nothing gives a value is asked for, not a reason to refuse.
- **A workspace runs in place unless you give it a branch** — `sprig up` without `-b` runs the repos where they're checked out, as `main`. The app's new-workspace form only makes worktree workspaces, so its "Where it runs" choice is gone; the checkout itself is the `main` row instead.
- **A workspace keeps its ports** — the form's port choice and `--random-ports` are gone. Fresh ports every time solved a problem nobody could name.
- **A repo's page in cards** — what it runs, its infra, its settings and what it offers are a card each, with the path, ports, readiness check and what it waits for on lines of their own instead of one long run of dots. **Remove…** moved into a ⋯ menu at the top right.
- **`sprig up` means up** — it starts the apps too, in the background, and waits until they're ready, where it used to stop at the infra and leave you to say `sprig start`. `--run` follows their logs instead, and `--no-apps` stops at the infra as before. `-d` is gone, being what `up` does now.
- **Inside a workspace's folder is inside the workspace** — `shop-web--feat-x` counts as feat-x, not just the `shop-web` worktree inside it, so `sprig settings` there no longer says the folder isn't on the Repos list.
- **How a workspace runs is the workspace's business** — which branch a new one starts from is chosen when you check it out, in the new-workspace form or with `sprig up -b … --from`, and the form remembers where you started from per orchestration or repo. Without `--from`, each repo branches from its own default branch, so a `master` repo and a `main` repo can share an orchestration without either pretending. `orchestration.Worktrees()` and `orchestration.Ports()` are gone: code that still calls them fails at that line with a note saying where the choice went.
- **Experimental branch: everything goes** — the old model, maps, pools, editors, tour and most of the CLI are gone. What's left is the app shell, Settings, About, the updater and `sprig open`/`update`/`shell`. Brave, or reckless. The roadmap will decide.
- **A store of its own** — this build keeps its data in `sprig-exp`, so it can't touch the released app's maps and workspaces.
- **Right-click an app to start, restart, stop or pin it** — the app list was too narrow for a row of buttons on every line, so it traded them for one menu, also behind a faint ⋮ at the end of each row for those who don't right-click on instinct. A small pin marks the pinned ones.
- **Up, down and remove can be cancelled** — the progress window has a Cancel button, and its close button cancels too, instead of holding you hostage while Docker stares into the middle distance. If the step ignores the cancel, sprig stops waiting for it after a few seconds and says so.
- **Finished jobs fade out** — still green, but dimmed, so a migration that went fine stops asking for your attention.
- **Urls in code read as strings** — the editor and the review dialog no longer draw them as links in a blue that vanished into the dark background. They're values to edit, not pages to visit.
- **Enter in a path box picks the suggestion** — with the list open, Enter takes the highlighted folder (or the first) instead of submitting the form with half a path, and Escape closes the list rather than the dialog. Tab still goes a folder deeper, and Enter on a path typed in full submits as before.
- **`Repo(path, definition:)` is gone** — a definition kept in sprig belongs to its repo's entry on the Repos page now, so there's one way to keep one rather than two. An orchestration that still uses it stops compiling, at that line. "Virtual" left with it, and nobody waved.
- **A new branch in a slot starts with no logs** — releasing or removing a slot deletes its apps' logs, and checking a branch out in a new or free slot clears any left behind, so the next branch no longer opens on the last one's output and leaves you debugging someone else's afternoon. The app's log pane lets go of the old lines too.
- **Model schema 13** — repos can have groups. A definition built with an older Sprig.Sdk is refused with a note to update its `#:package` line.

---

## [v0.8.2] - 2026-11-02

### Changed
- **Updating greys out the window** — progress shown centre stage. No more wondering if the click took.

---

## [v0.8.1] - 2026-11-02

### Added
- **The Maps page draws the wiring** — unmet needs in red. Checkout is no longer the first to know.
- **The graph is a canvas** — drag to pan, Ctrl+scroll to zoom. No scrollbars were harmed.
- **Maps are composed on the graph** — New map, then Add repo. Checkboxes retired.
- **One list of variables** — expand a row to link it or pin it.
- **Map constants** — beat any provider, shown in violet. Set with `sprig map constant`.
- **Hard-coded values flagged** — `localhost:1025` becomes a need in one click.
- **`sprig map check`** — exits 1 when a checkout would stop, and says why.

### Changed
- **`sprig cd` shows the workspace label** — `pool-03` stops being a mystery.
- **`sprig map show` reports the wiring** — rather than reciting the file.
- **Map wiring can name the repo** — `"billing/cache"` settles a tie. The error's suggested fix now works.

### Removed
- **Checking out from the Maps page** — the Workspaces page does it.

### Fixed
- **Editing a map in the app kept only name and repos** — wiring and constants now survive.

---

## [v0.8.0] - 2026-10-30

### Changed
- **Claude Code plugin speaks maps** — it was still asking for stacks.
- **`/sprig:sprig-create` → `/sprig:sprig-checkout`**, plus a new `/sprig:sprig-release`.
- **The app's infra "Reset" is "Restart"** — matching the CLI.
- **`sprig ws create` is no longer deprecated** — for single repos and map slices.
- **Hints suggest commands that exist.**
- **README and user guide catch up** — no stacks, and every workspace verb has its `ws`.

### Removed
- **`sprig ws reset`** — it outstayed its one release. Use `sprig ws restart`.

---

## [v0.7.5] - 2026-10-30

### Added
- **Edit as JSON** — paste or load a virtual config. Sometimes the form is the long way round.

---

## [v0.7.4] - 2026-10-30

### Added
- **Virtual repos** — the config lives in sprig. Try before you commit.
- **One checkout, many configs** — register it under several names.
- **"virtual · not committed" badges** — on repos and their workspaces.
- **Write to repo** — a finished config becomes a `.sprig.json`.

### Changed
- **Workspace variables, one per line** — selectable, and no longer said twice.
- **Install shrinks from 109 MB to 87 MB** — it stopped packing what sprig never calls.
- **`sprig.exe` is a single file** — brings its own runtime. No slower for it.

---

## [v0.7.3] - 2026-10-30

### Added
- **Claude Code worktree layout** — `--worktree-layout claude` nests worktrees in `.claude/worktrees`.
- **Sibling path kept as a junction** — for muscle memory.
- **`reconcile` checks the junction** — rebuilds it; never touches a real folder.

### Fixed
- **Saving ports keeps your other settings** — the changelog toggle had been quietly reverting.
- **`init` ignores nested checkouts** — a worktree inside the repo isn't more repo.

---

## [v0.7.2] - 2026-10-30

### Fixed
- **Release clears the label and note** — no more inheriting "waiting on review".
- **Free workspaces stop wearing their last label** — it's a hint in the reuse picker now.

---

## [v0.7.1] - 2026-10-30

### Added
- **`sprig shell install`** — reruns shim setup once you allow scripts.

### Changed
- **The installer adds the `sprig cd` shims** — PowerShell and cmd move in place.
- **PowerShell's shim needs scripts allowed** — `sprig shell install` names the culprit.

---

## [v0.7.0] - 2026-10-29

Stacks are gone. Repos now describe what they **provide** and **need**, and **maps** wire slices of them together by capability name. Schema v1 starts fresh, with no migration: recreate stack-era workspaces by hand. Workspaces also finally say what they're for, rather than leaving you to decode branch names.

### Added
- **Self-describing repos** — per-module `provides` and `needs`, referenced as `${sprig.<capability>.<output>}`.
- **Maps** — compose a slice of repos, storing only `wiring` and `defaults`.
- **Nearest-wins wiring** — a sibling module first, then the map; gaps listed.
- **Pooled workspaces** — `sprig pool checkout`, `release` and `status`, capped by `maxSlots`.
- **`sprig ws refresh`** — resync to base, keeping installed deps.
- **Degraded workspaces** — failed setup is flagged rather than handed over as healthy.
- **Labels and notes** — `sprig ws label` and `sprig ws note`, editable any time.
- **`sprig here`** — which workspace, repo and module you're in. `--json` is versioned.
- **Tags** — `ws tag`, `ws untag`, `ws ls --tag`. Sprig stores them and asks no questions.
- **`sprig ws switch`** — onto a new branch, an existing one or a ref. The old branch is never touched (sprig can't tell if your PR merged, and won't guess).
- **Detached checkout** — `pool checkout --detached`, for reviews and repros.
- **Entry points** — `open` and `probe` on a capability; `ws urls [--probe]` and `ws browse`.
- **`sprig code`** — one VS Code window per workspace, titled and colour-tinted.
- **`sprig ws colour`** — pick a workspace's colour; each slot has a default.
- **`sprig cd` moves your shell** — via PATH shims in PowerShell and cmd. The installer doesn't add them yet.
- **Examples sandbox** — `scripts/sandbox` builds tiny repos to test against. Real codebases may stand down.

### Changed
- **`ws ls` and app cards lead with the label** — slot, branch and note beneath.
- **Branches read live** — `git switch` in a worktree no longer fools sprig.
- **`sprig create` is map-based** — `--map` or `--repo`.
- **`sprig ws reset` → `sprig ws restart`** — `reset` lingers as an alias for one release.
- **Release stops containers** — it no longer tears down networks and volumes.
- **`sprig cd` titles its window** under Windows Terminal.
- **The app honours `SPRIG_STORE`**, as the CLI always has.

### Removed
- **Stacks** — the command, the builder canvas, shared ports and repo `inputs`.

### Deprecated
- **`sprig ws create`** — use `sprig pool checkout`.

---

## [v0.6.2] - 2026-08-09

### Added
- **Claude Code plugin** — configure, create and tear down workspaces without leaving the editor. Lives in `plugins/sprig`; the app and CLI are untouched.

---

## [v0.6.1] - 2026-08-09

### Added
- **Clone a stack** — right-click a stack, pick "Clone…", name the copy. A slight variation no longer means wiring one from scratch.

---

## [v0.6.0] - 2026-08-09

### Added
- **`sprig path`** — prints a workspace's repo/module directory, for scripts and shell wrappers.
- **`--no-interactive` / `--ni`** — the "never prompt, just fail" opt-out to `-i`.

### Changed
- **Interactive by default at a terminal** — run a command bare and it walks you through the choices; scripts, pipes and CI stay non-interactive and fail fast instead of hanging.
- **`cd`/`path` fill in the blanks** — name what you know, get asked for the rest.
- **`sprig cd` is navigate-only** — it opens a window; `--print`/`--json` moved to `sprig path`.
- **`sprig ws rm <name>` confirms at a terminal** — `--yes` is now only for skipping the prompt (and still required in scripts).
- **Consistent, coloured output** — green success ticks, colour-coded drift state, aligned tables. `--json` stays plain.

---

## [v0.5.6] - 2026-08-09

### Changed
- **Hand-added compose files get auto-detected too** — point a new compose row at a real file and sprig proposes the same container-name/port rewrites, and declares their inputs, as on first add. Files you loaded or overrides you've edited are left alone.

---

## [v0.5.5] - 2026-08-09

### Fixed
- **Real `.env` values now win over the template** — a worktree seeds from your actual gitignored file, not the placeholder. The template only stands in when the file's genuinely absent.

---

## [v0.5.4] - 2026-08-08

### Changed
- **A teardown that can't finish is now kept, not lost** — flagged in the list instead of vanishing half-dismantled.
- **`sprig ws rm` is idempotent** — re-run after fixing the blocker and it finishes the sweep (deleting the already-deleted is fine).
- **A stopped Docker no longer eats the workspace mid-teardown** — containers weren't stopped, so the record stays for a retry.
- **`ws ls`/`ws info` call out a failed teardown**, and the app badges it — so you know what still needs a retry.

---

## [v0.5.3] - 2026-08-08

### Changed
- **`sprig create` now starts the workspace's infra** — matching the app. `--skip-infra` leaves it created-only.
- **A stalled Docker no longer fails a create** — the workspace is kept, with a nudge toward `sprig ws up`.

---

## [v0.5.2] - 2026-08-08

### Added
- **`sprig cd`** — open a new terminal in a workspace's repo or module, in the shell you came from. `-i` picks interactively; `--print` if you only want the path.

---

## [v0.5.1] - 2026-08-08

### Added
- **`sprig ws create -i`** — pick the stack, repos, modules and name interactively (esc steps back).
- **`sprig ws rm -i`** — pick a workspace to destroy, with confirmation.
- **Per-command help** — `sprig stack --help` lists that group's own commands.

### Changed
- **Workspace verbs now live under `ws`** — `sprig ls` becomes `sprig ws ls`.
- **Create and destroy show a live checklist**, grouped per repo — no longer just a frozen pause.
- **List commands render as tables** — coloured in a terminal, plain when piped.
- **The app's workspace "Remove" button is now "Destroy"** — same button, more terminal.

---

## [v0.5.0] - 2026-08-07

### Added
- **`sprig settings`** — view and set the port range and restricted ports from the terminal.
- **`sprig stack edit`** — amend a stack in place, instead of deleting and recreating it.
- **`ws`/`workspace` prefix on workspace verbs** — `sprig ws ls`, for anyone who likes their nouns first.

### Changed
- **`--json` works on every command now**, not just the ones that felt like it.
- **`sprig info` shows the whole workspace in one place** — repos, ports, drift, and live containers.
- **Stricter arg parsing** — `--flag=value` works, `--` ends options, and a typo'd flag errors instead of being quietly ignored.
- **Retired `sprig templates`** — it was `stack ls` in a hat.

---

## [v0.4.10] - 2026-08-06

### Changed
- **Selectable compose values are grey, not green** — colour is saved for values you've actually replaced.

---

## [v0.4.9] - 2026-08-06

### Added
- **Drag to reorder sources and repos** on the wiring canvas — the order sticks.
- **"Tidy" button** — reorders sources and repos to cut cable crossings.
- **Hover a repo to dim everything it isn't wired to** — a busy stack, untangled at a glance.

### Changed
- **The stack editor opens in a resizable window** — the canvas grows with it, not a cramped overlay.
- **Guided coachmarks block click-through** — advance with their own buttons, not the UI underneath.

---

## [v0.4.8] - 2026-08-06

### Fixed
- **New stack opens on a clean canvas** — a previous session's ports no longer linger there.

---

## [v0.4.7] - 2026-08-06

### Changed
- **`sprig update` narrates itself** — names the feed it's querying instead of sitting silent through the slow bit.
- **Workspace branches are flat `sprig--<workspace>`** (was `sprig/<workspace>`) — a stray `sprig` branch no longer wedges the whole namespace.

### Fixed
- **Create checks the target branch first** — a pre-existing branch is flagged in the form, not a raw git `fatal` mid-run.

---

## [v0.4.6] - 2026-08-06

### Added
- **`sprig update`** — install a newer release from the terminal (`--check` just reports one).

### Changed
- **Update notice moved to the nav** — a quiet entry above Settings, not a banner; click to install.
- **"Show me a working setup" lives in Learn** — beside the lessons, off the nav.

---

## [v0.4.5] - 2026-08-06

### Fixed
- **Compose editor no longer hides its last line** — the bottom padding scrolls into view.

---

## [v0.4.4] - 2026-08-06

### Added
- **`sprig open`** — launch the desktop app from the terminal, for when you want something more hands-on.

---

## [v0.4.3] - 2026-08-06

### Added
- **The `sprig` CLI ships with the installer** — bundled with the app and added to your PATH, so `sprig --help` works from any new terminal. Removed on uninstall; no admin needed.

---

## [v0.4.2] - 2026-08-03

### Added
- **"Update now" on the update banner** — install the new version in place, no trip to the About page.

### Changed
- **Dismissing the update banner sticks** — it stays gone until a genuinely newer release turns up.
- **The update banner keeps out of lessons** — hidden during the tour and guides so it can't shove the coachmarks off their targets.

---

## [v0.4.1] - 2026-08-02

### Added
- **New Learn lesson: "Split a repo into modules"** — a hands-on monorepo walkthrough in the sandbox.

### Changed
- **Coachmark callouts no longer cover what they're explaining** — they park on a side with room.
- **The learning path threads modules in** — the first lesson now hands off to the new one.

---

## [v0.4.0] - 2026-08-01

### Added
- **Monorepo support** — a repo can declare many **modules**, each with its own `.env`, compose and setup.
- **Each module gets a `path`** — the subdirectory it lives in; setup runs there and its paths resolve under it.
- **Modules are tabs** — in the repo preview and editor, with **+ Add module** and delete, down to zero.
- **Inputs stay shared, pinned above the tabs** — declared once, referenced from any module, no duplicating.
- **Adding a repo asks: one module or many** — define each module's path and name; sprig autodetects each.
- **Module path fields suggest directories** — click one and it lists subfolders, no typing required.
- **File fields autocomplete within the module** — `.env`, compose and template paths suggest from the module's directory.
- **One-line installer** — `irm https://raw.githubusercontent.com/ArcticGizmo/sprig/main/install.ps1 | iex`; no admin, self-updates in-app.
- **Verified downloads** — the installer checks each release against its published `SHA256SUMS.txt` and refuses a mismatch.

### Changed
- **Schema 3** — `env`/`compose`/`setup` moved inside modules. Old files fold into a single `app` module on load (rewritten on next save).
- **`sprig init` emits schema 3**, and the CLI groups a workspace's setup output by module.
- **`${sprig.*}` autocomplete triggers on `$`** and matches any dot-segment.
- **Module editor polish** — section cards, prominent tabs, and a live path-exists check.

---

## [v0.3.3] - 2026-07-28

### Added
- **Partial workspaces** — untick the repos you don't need before creating.
- **A deselected repo gets nothing** — no worktree, no `.env`, no compose.
- **Ports left with no consumer aren't provisioned** — sprig names them before you commit.
- **`sprig create feat --stack web+api --without web`** (or `--only api`) for the terminal.
- **Guided tour** — "Show me a working setup" spins up a throwaway sample to explore, then deletes it.
- **Spotlight coachmarks** — the tour dims the page and rings each thing as it explains it.
- **Learn library** — four hands-on lessons: register a repo, wire a stack, run a workspace, recover from drift.
- **All in a throwaway sandbox** — a separate demo store, never your real repos, gone when you leave.
- **A licence** — MIT, in case anyone was waiting on that.

### Changed
- **The repo editor explains what sprig scaffolded** for you, instead of a pre-filled form with no backstory.
- **The stack builder opens pre-wired** — review the auto-wiring instead of a blank canvas.
- **"Example" is now "Example shape"** — it's documentation for the stack, never a value.
- **Port restrictions tuck behind a "restrict…" link** until you actually want them.

---

## [v0.3.2] - 2026-07-26

### Changed
- **Updates now come from GitHub Releases by default** — no environment variable needed to find them.

---

## [v0.3.1] - 2026-07-26

### Added
- **Setup commands.** A repo declares `setup` steps (`npm ci`, `dotnet restore`) that run in the fresh worktree on create.
- **A failed install warns instead of rolling back** — the worktree stays put so you can finish it by hand.
- **Create and teardown get a live progress window** — a non-blocking checklist, one row per step, that you can leave open while you carry on.
- **Each step shows its state at a glance** — waiting, running, done, warned, or failed.
- **Install commands appear as sub-items, streaming their output** — watch each one scroll past instead of guessing whether it hung.

---

## [v0.3.0] - 2026-07-25

### Added
- **Visual stack builder.** Drag repos, ports, and cables on a canvas instead of filling in a form.
- **Auto-wire.** One click maps every input to a port by convention, leaving anything you typed alone.
- **Transform nodes.** Fold several ports into one value — URLs, connection strings — with a per-line expression editor.
- **Shared ports.** Point two repos at one port on purpose; renaming it follows every binding.
- **Clickable env overrides.** Set a value straight from the file view, matching the compose overlay.
- **Undeclared inputs show as quick-add chips** — reference `${sprig.*}` first, declare it after.
- **One-click template guesses** for hard-coded local ports in URLs and connection strings.
- **"Delete config"** resets a repo's sprig state.

### Changed
- **The canvas is the only stack builder now — the form is retired.**
- **Saving a stack clears ports nothing uses.**
- **The repo picker refreshes when the registry changes.**

---

## [v0.2.0] - 2026-07-23

### Added
- **Per-input port restrictions.** Pin an input to fixed host ports with `"allowedPorts": "8100-8103"` — for the Auth0 callbacks you can only pre-register so many of.
- **Seed env files from templates.** An env override can seed from committed files like `.env.template` instead of clobbering with an empty one.
- **Auto-start Docker infra on workspace create.** New workspaces bring their containers up straight away; the detail view shows live container status.
- **Import/export stacks in the app.** Share a stack as a `.json` file, with a nudge to register any repos this machine hasn't met.
- **Dev builds get their own store.** A dev build uses `sprig (Dev)`, wears a pink `DEV` badge, and won't clobber your installed copy's data.

### Changed
- **A repo can override several compose files.** `compose` is now an array (monorepos keep several). **Breaking:** schema is now `2` — rewrite `"compose": { … }` as `"compose": [ { … } ]`, or re-run `sprig init`.
- **Adding a repo drops you straight into its editor** — no more returning to the list with the config a step away.
- **Scaffolding only overrides env files it can safely clobber.** Untracked files get overridden; committed ones become seed templates instead.

---

## [v0.1.0] - 2026-07-22

The first release. sprig runs isolated copies of your repos in parallel, so you can build a feature and a hotfix side by side without them colliding.

- Spin up isolated copies of one or more repos 
- Each workspace gets its own git **worktree** and branch, with your main checkout left untouched.
- Each workspace gets **non-colliding ports** (from a configurable range, with ports you can mark off-limits) and its own **docker infra** via a generated compose file.
- **Stacks** wire several repos together — a repo declares the inputs it needs, the stack supplies every value.
- **Drift-safe** — detect and repair a deleted or orphaned worktree, so a half-cleaned-up workspace is always recoverable.
- **Desktop app** with a guided Home → Repos → Stacks → Workspaces journey, port settings, and update notifications with in-app install.
- **CLI** covering everything the app does, with `--json` on read commands for scripting.
