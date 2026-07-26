# Changelog

All notable changes to sprig are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added
- **`sprig plan`** — see every value a workspace resolves to and which layer set it, before you create anything. Pass `--stack` for a dry run (nothing is allocated, ports show as `{name}` placeholders) or a workspace name to re-plan one that already exists.
- **Shared resources (groundwork).** A machine-local overlay can now pool infrastructure across workspaces by rewriting values on their way to the worktree — without changing a single line of `.sprig.json` or of a stack. Define one in `shared/`, list it with `sprig shared ls`, and see exactly what it changes in `sprig plan`. The containers themselves aren't managed yet.
- **`--no-shared`** on `create` and `plan` — build a workspace with private infrastructure as though the feature didn't exist.
- **A shared resource switches off the repo's own copy.** The service it replaces is left out of the generated compose, along with the `depends_on` entries and volumes that would dangle without it — so nothing runs twice, and nothing fails to start.
- **Shared resources now run.** One container serves several workspaces, each with its own database: the first workspace to come up starts it, the last one down stops it, and `sprig rm` drops just that workspace's data. A stopped workspace keeps its database, exactly as it keeps its worktree.
- **A pool that's full tells you who's holding it** — oldest first, with three ways out. Slots belonging to workspaces that no longer exist are reclaimed rather than reported.
- **`sprig shared up | down | reclaim`**, and `down`/`rm` refuse while workspaces are still attached.

### Changed
- **A stack port nothing binds to is no longer reserved.** It never did anything; now it doesn't hold a number hostage either.

---

## [v0.3.3] - 2026-07-27

### Added
- **Guided tour** — "Show me a working setup" spins up a throwaway sample to explore, then deletes it.
- **Spotlight coachmarks** — the tour dims the page and rings each thing as it explains it.
- **Learn library** — four hands-on lessons: register a repo, wire a stack, run a workspace, recover from drift.
- **All in a throwaway sandbox** — a separate demo store, never your real repos, gone when you leave.

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
