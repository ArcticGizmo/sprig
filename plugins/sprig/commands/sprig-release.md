---
description: Release a claimed sprig workspace back to its pool (stops containers, removes nothing)
argument-hint: "<workspace>"
allowed-tools: Bash(sprig:*)
---

Release the workspace `$ARGUMENTS` using the **sprig-teardown** skill's release procedure.

- If no workspace was named, run `sprig ws ls --json`, list the claimed ones (`Claimed: true`) with their `Label`, and ask which.
- Run `sprig pool release --workspace <workspace> --json --ni`.
- Report any `pending[]` entries (uncommitted changes, unpushed commits) and where that work now lives. For a detached claim, warn that its commits are on no branch.
