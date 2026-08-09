---
description: Detect (and optionally repair) sprig workspace drift
argument-hint: "[workspace] [--repair]"
allowed-tools: Bash(sprig:*)
---

Check for record-vs-reality drift with `sprig ws reconcile $ARGUMENTS --json` (omit the workspace to
check all; `reconcile` is aliased as `doctor`).

Report each workspace as healthy / drifted / gone, showing what drifted (e.g. an orphaned or deleted
worktree). If the user wants it fixed, re-run with `--repair` and list the repairs applied. For
destroying workspaces use the sprig-teardown skill instead.
