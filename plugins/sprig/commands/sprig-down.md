---
description: Stop a sprig workspace's infra (optionally wiping volumes)
argument-hint: "<workspace> [--volumes]"
allowed-tools: Bash(sprig:*)
---

Stop the workspace's infra with `sprig ws down $ARGUMENTS`. This **keeps** the workspace, worktree and
record — it only stops containers.

If the user passed (or asks for) `--volumes`, that **wipes docker data** — confirm they mean it before
running. To destroy the workspace entirely (not just stop it), use the sprig-teardown skill / the
`/sprig:sprig-rm` command instead.
