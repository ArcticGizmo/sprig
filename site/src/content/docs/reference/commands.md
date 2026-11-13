---
title: Commands
description: Every sprig command, in one table.
---

Run `sprig <command> --help` for a command's options. Most commands work out the repo and workspace from the folder you're in: the repo's own checkout means `main`, and a slot's worktree means that slot.

## Workspaces

| Command | What it does |
| --- | --- |
| `sprig init` | Make the repo in this folder ready to run: its own `sprig.yaml`, or a draft from its projects. |
| `sprig up` | Bring a workspace up: `-b <branch>` for a slot of worktrees, or `main` without it. Then its apps, in the background. |
| `sprig plan` | Show everything a workspace would get, and where each value comes from. |
| `sprig info` | Show a workspace whole: where it is, its apps and infra and their state, and its inputs. |
| `sprig workspaces` | List workspaces. |
| `sprig down` | Stop a workspace's infra, keeping its data, worktrees and ports. |
| `sprig release` | Free a slot for another branch, keeping its branch, data, ports and values. Also `sprig free`. |
| `sprig empty-data` | Empty a workspace's data and bring it back as it was. |
| `sprig rm` | Remove a workspace: its containers and their data, its worktrees, and the values it wrote. |

## Apps

| Command | What it does |
| --- | --- |
| `sprig start` | Start a workspace's apps and jobs in dependency order, and wait until each is ready. |
| `sprig restart` | Build and restart the running apps. Stopped apps stay stopped. |
| `sprig stop` | Stop a workspace's apps. Its infra keeps running. |
| `sprig ps` | List the apps that are running. |
| `sprig logs` | Print the end of an app's log. |

## Inputs

| Command | What it does |
| --- | --- |
| `sprig inputs` | List each input, its value, and where the value comes from. |
| `sprig set` | Give a workspace's inputs values, kept for next time. |
| `sprig unset` | Clear values set on a workspace, so the default applies again. |

## Repos, orchestrations and approval

| Command | What it does |
| --- | --- |
| `sprig repo add \| copy \| ls \| rm` | The Repos list: repo folders that orchestrations use by ID. |
| `sprig orchestration add \| ls \| rm` | Register the orchestration files that put repos together. |
| `sprig trust add \| ls` | Approve definition files to run, by exact content. |

## The app and sprig itself

| Command | What it does |
| --- | --- |
| `sprig open` | Launch the desktop app. |
| `sprig update` | Install a newer release in place. `--check` only reports. |
| `sprig host status \| stop` | The background process that runs workspaces' apps. |
| `sprig shell install \| shims` | Shell integration: PATH entries and shims. |

`--json` on a command gives machine-readable output. Secrets are hidden in all output unless you pass `--unmask-secrets`.
