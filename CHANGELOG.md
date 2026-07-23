# Changelog

All notable changes to sprig are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added
- **Seed env files from templates.** An env override can now name one or more `templates` — committed files like `.env.template`/`.env.example` — that seed the worktree's copy before sprig injects its override block. Previously the seed was always the target file's own content, which was empty whenever the real file was gitignored and never committed, so any hand-maintained defaults were lost. Multiple templates concatenate in order; missing ones are skipped. Configurable per env file in the repo editor ("Seed from templates"), with a ✓/⚠ found indicator.
- **Auto-start infrastructure on workspace create.** Creating a workspace now brings its Docker infra up straight away (toggle off with the "Start its Docker infrastructure now" checkbox in the create dialog). If Docker isn't running the workspace is still created — you just get a soft warning. The workspace detail also shows a live **Docker** section: each container with its state (green when running), a **Refresh status** button, and **Open in Docker** to jump to Docker Desktop.
- **Per-input port restrictions (`allowedPorts`).** A repo can now pin one of its inputs to a fixed set of host ports — set `"allowedPorts": "8100-8103"` (ranges and comma lists both work) on an input in `.sprig.json`. sprig traces that input through the stack binding to the port that feeds it and only ever allocates from the allowed set. The motivating case: an Auth0 front end whose callback URLs are pre-registered per port — you can only run as many instances as you've registered ports, so sprig now respects that ceiling and fails a create with a clear "no free callback port left" message rather than handing out a port Auth0 will reject. Editable per input in the app's repo editor.
- **Dev instances keep their own store.** A development build (F5 / `dotnet run` / `dotnet test`) now reads and writes `%LOCALAPPDATA%\sprig (Dev)` instead of the release's `%LOCALAPPDATA%\sprig`, so hacking on sprig can't clobber the repos, stacks, ports and workspaces your installed copy depends on. The `SPRIG_DEV` environment variable overrides the build default (`SPRIG_DEV=0` points a debug build at the real store; any other value forces a dev store). A dev instance is unmistakable: a pink `- DEV` badge sits atop the nav next to the sprig wordmark, and the window title carries a `(Dev)` suffix.
- **Import/export stacks in the desktop app.** Stacks live in the central store, not in any repo, so there was no way to share one — now you can export a stack to a `.json` file from its detail card and import one back from the Stacks header. On import, if the stack names repos this machine doesn't know yet, sprig tells you exactly which ones and offers a one-click jump to register them (stacks reference repos by name, so paths stay machine-local). The CLI already had `stack export`/`stack import`; this surfaces the same thing in the app.

### Changed
- **A repo can now override several docker-compose files.** `compose` in `.sprig.json` is now an **array** — a repo may isolate more than one compose file (monorepos routinely keep several). `sprig init` discovers compose files recursively (skipping build/vendor dirs like `node_modules`, `dist`, `obj`) and proposes an entry for each; the repo editor gives every compose file its own card with its own interactive overlay, and a "+ Add compose file" / remove flow that works exactly like the env section — remove a card to say "don't override this one." At create time sprig generates one isolated compose file per entry and brings them all up together under the workspace's single docker-compose project. **Breaking:** the config schema is now `2`, and `"compose": { … }` (a single object) is no longer accepted — rewrite it as `"compose": [ { … } ]`, or just re-run `sprig init`. Existing `.sprig.json` files stay on schema 1 until updated and will be flagged as unsupported.
- **Adding a repo opens its configuration for editing.** The Add-repo dialog now drops you straight into the repo editor once it registers — "Load & edit" when the folder already has a `.sprig.json`, "Create & edit" when sprig scaffolds one. Previously it returned you to the list with only a status line, leaving the config a step away.
- **Scaffolding now targets the env files that are safe to override.** When sprig proposes a `.sprig.json` for a new repo it no longer defaults to `.env` regardless of whether that file is the right one. It scans the repo (recursively, skipping `node_modules`, `dist`, `obj` and friends) and only proposes overrides for **untracked** env files — the ones it can safely clobber. A committed `.env`/`.env.example` sitting next to an untracked `.env.local` is offered as that file's seed **template** instead of being overridden. Port-shaped values are detected from the target *and* its templates, so isolation is set up even when the real values only live in the committed template. Repos with nothing but tracked env files get an empty env section to fill in by hand rather than a bogus default.

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
