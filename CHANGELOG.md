# Changelog

All notable changes to sprig are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

### Added
- **Dev instances keep their own store.** A development build (F5 / `dotnet run` / `dotnet test`) now reads and writes `%LOCALAPPDATA%\sprig (Dev)` instead of the release's `%LOCALAPPDATA%\sprig`, so hacking on sprig can't clobber the repos, stacks, ports and workspaces your installed copy depends on. The `SPRIG_DEV` environment variable overrides the build default (`SPRIG_DEV=0` points a debug build at the real store; any other value forces a dev store). A dev instance is unmistakable: a pink `- DEV` badge sits atop the nav next to the sprig wordmark, and the window title carries a `(Dev)` suffix.
- **Import/export stacks in the desktop app.** Stacks live in the central store, not in any repo, so there was no way to share one — now you can export a stack to a `.json` file from its detail card and import one back from the Stacks header. On import, if the stack names repos this machine doesn't know yet, sprig tells you exactly which ones and offers a one-click jump to register them (stacks reference repos by name, so paths stay machine-local). The CLI already had `stack export`/`stack import`; this surfaces the same thing in the app.

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
