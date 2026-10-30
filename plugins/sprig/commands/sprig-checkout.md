---
description: Check out a sprig workspace from a map's pool, on a branch, with a label
argument-hint: "[map] [--branch <name> | --detached] [--label <text>] [--from <ref>] [--fresh]"
allowed-tools: Bash(sprig:*)
---

Check out a sprig workspace using the **sprig-workspace** skill. Requested arguments: `$ARGUMENTS`.

- If no map was given, run `sprig map ls --json`. Use the only map if there's one; otherwise ask which.
- If neither `--branch` nor `--detached` was given, ask for the branch. If no `--label` was given, propose one from what the user is working on.
- Run `sprig pool checkout <map> … --json --ni`, passing through `--branch`/`--detached`, `--label`, `--from`, `--fresh`/`--keep`, `--workspace` and any `--tag`.
- Report the workspace name, label, allocated `Ports`, entry-point URLs (`sprig ws urls <ws> --json`) and each repo's `WorktreePath`. Flag `SetupFailed` (degraded: finish setup by hand in the worktree).
- A full pool: list the claimed workspaces and ask which to release (`/sprig:sprig-release`).
- An unmet or ambiguous need: hand off to the sprig-configure skill.
- For a single repo or a slice of a map, use `sprig ws create <name> --repo <path>` or `--map <map> --only a,b` instead.
