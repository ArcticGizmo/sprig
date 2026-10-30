---
description: Detect (and optionally repair) sprig workspace drift
argument-hint: "[workspace] [--repair]"
allowed-tools: Bash(sprig:*)
---

Check for record-vs-reality drift with `sprig ws reconcile $ARGUMENTS --json` (omit the workspace to check all; `reconcile` is aliased as `doctor`).

Report each workspace as healthy / drifted / gone, showing what drifted (an orphaned or deleted worktree, or under the Claude Code layout a missing, dangling or mis-aimed junction). If the user wants it fixed, re-run with `--repair` and list the repairs applied. A junction reported as blocked (a real folder in the way) is never repaired — tell the user what's there. For destroying workspaces use the sprig-teardown skill instead.
