---
description: Stop a sprig workspace's infra (optionally wiping volumes)
argument-hint: "<workspace> [--volumes]"
allowed-tools: Bash(sprig:*)
---

Stop the workspace's infra with `sprig ws down $ARGUMENTS --json`. This **keeps** the workspace, worktree, record and claim — it only stops containers.

If the user passed (or asks for) `--volumes`, that **wipes docker data** — confirm they mean it before running. If they're finished with a pooled workspace, `/sprig:sprig-release` is usually what they want. To destroy the workspace entirely, use the sprig-teardown skill / `/sprig:sprig-rm` instead.
