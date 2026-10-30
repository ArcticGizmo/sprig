---
description: Bring a sprig workspace's docker infra up
argument-hint: "<workspace>"
allowed-tools: Bash(sprig:*)
---

Run `sprig ws up $ARGUMENTS --json` to start the workspace's docker infra. A workspace with no compose files is a silent no-op. If it fails (e.g. Docker not running), report the error — the workspace record itself is unaffected. Use `sprig ws restart $ARGUMENTS --json` instead if the user wants a restart (down then up, keeping volumes).
