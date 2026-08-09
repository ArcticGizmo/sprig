---
description: Destroy a sprig workspace (routes through the safe teardown skill)
argument-hint: "<workspace>"
allowed-tools: Bash(sprig:*)
---

Destroy the workspace `$ARGUMENTS` using the **sprig-teardown** skill — do not shell straight to
`sprig ws rm --yes`.

Follow the skill's procedure: state what will be destroyed (worktrees removed, volumes wiped, infra
stopped), **ask explicitly** whether to also delete the git branch (`--force`, which loses any commits
in the worktree), then run `sprig ws rm <workspace> --yes --json` (with `--force` only if confirmed).
If the result reports `teardownFailed`, surface the `issues[]` and explain the idempotent retry.
