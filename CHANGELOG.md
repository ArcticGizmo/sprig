# Changelog

All notable changes to sprig are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

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
