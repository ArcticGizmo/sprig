---
description: List sprig workspaces (and drill into one) with purpose, ports, URLs, drift and container status
argument-hint: "[workspace | map]"
allowed-tools: Bash(sprig:*)
---

With no argument, run `sprig ws ls --json` and present a concise table led by purpose: workspace, label (or the live branch from `Repos[].Head.Branch` when unlabelled), claimed/free, ports, status. Call out any workspace with `TeardownFailed` or `SetupFailed` (degraded).

If `$ARGUMENTS` names a map (check `sprig map ls --json`), run `sprig pool status $ARGUMENTS --json` and summarise claimed / free / degraded against `maxSlots`.

If it names a workspace, run `sprig ws info $ARGUMENTS --json` and summarise its label and note, allocated ports, entry-point `urls`, each repo's live branch and worktree path, any drift, and live containers (report "docker unavailable" if `containers` is null).

Check the exit code before parsing; on failure show the `error` from the JSON payload.
