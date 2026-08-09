---
description: List sprig workspaces (and drill into one) with ports, drift and container status
argument-hint: "[workspace]"
allowed-tools: Bash(sprig:*)
---

Run `sprig ws ls --json` and present a concise table: workspace, repos, ports, status. Call out any
workspace whose status is `teardownFailed`.

If a workspace was named (`$ARGUMENTS`), also run `sprig ws info $ARGUMENTS --json` and summarise its
allocated ports, each repo's worktree path, any drift, and live containers (report "docker
unavailable" if `containers` is null).

Check the exit code before parsing; on failure show the `error` from the JSON payload.
