# Changelog

All notable changes to sprig are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

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
