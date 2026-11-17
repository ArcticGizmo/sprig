---
name: sprig-workspaces
description: Run a repo's apps and infra with sprig, in the main checkout or in a worktree slot of their own. Use when the user asks to start, stop, restart or check on apps in a sprig repo, run a branch in its own slot, read an app's log, find the port or url an app listens on, or give a slot back.
---

# Run sprig workspaces

A sprig workspace is a repo's apps and infra, run either in the repo's own checkout (`main`) or in a numbered slot of worktrees with its own ports, env files and data, so several branches can be up at once.

## Before anything

Run `sprig docs agent` and follow it: it's the guide for the sprig installed here. If `sprig` isn't found, tell the user to install it (https://arcticgizmo.github.io/sprig/getting-started/install/). If `docs` isn't a command, ask them to run `sprig update`.

## How to work

- **Pass `--json`** to any command whose output you read, and branch on `code` when `ok` is false. Without a terminal sprig never prompts; the error says which flag answers it.
- **Start from what's here:** `sprig info --json` in the folder says which repo and workspace it is, each app's state, and its ports.
- **Starting waits until apps are ready.** `sprig start [app…] --json` returns once each is healthy, or says which wasn't. Then `sprig logs <app> -n 200` says why.
- **To run one app yourself** (to debug it, or with your own changes), `sprig stop <app>` and run it from its folder (`dotnet run`, `pnpm dev`). Sprig writes its values into files the app reads, so it gets the same port and addresses, and the rest keeps running.
- **A branch of your own:** `sprig checkout -b <branch> --json` checks it out in a free slot and makes it ready (worktrees, ports, files) without starting anything. `sprig path <branch>` prints the slot's folder.
- **Start only what you need running.** Checking out is cheap; starting builds every app and brings its infra up. When the work needs it running, `sprig start <app> --json` starts that app and what it depends on, and `sprig up <branch> --json` starts everything.
- **Give back what you took:** `sprig free` stops a slot and frees it for the next branch, keeping its data. Don't `sprig rm` (it deletes data and worktrees) or stop the user's main checkout unless they ask.
