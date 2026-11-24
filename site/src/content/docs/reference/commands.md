---
title: Commands
description: Every sprig command, in one table.
---

Run `sprig <command> --help` for a command's options. Most commands work out the repo and workspace from the folder you're in: the repo's own checkout means `main`, and a slot's worktree means that slot.

## Workspaces

| Command | What it does |
| --- | --- |
| `sprig init` | Make the repo in this folder ready to run: its own `sprig.yaml`, or a draft from its projects. |
| `sprig checkout -b <branch>` | Check a branch out in a slot of worktrees and make it ready to run: its ports, files and infra's definitions. Nothing starts. |
| `sprig up` | Run a workspace that's checked out: its apps in the background, after the infra they use. Names the workspace by its branch or slot number, or with none named, the slot you're in, else `main`. `--no-wait` returns once the apps are starting. |
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
| `sprig stop` | Stop what a workspace runs: the apps or infra you name, or with none named, everything. Containers keep their data. |
| `sprig pause` | Stop everything a workspace runs, keeping its data, and remember what was running. |
| `sprig resume` | Start again what was running when the workspace was paused, and wait until each is ready. |
| `sprig wait` | Wait until a workspace's apps are ready, as after `sprig start --no-wait`. With none named, every app it has going. |
| `sprig ps` | List the apps that are running and the workspaces' containers, with their CPU and memory. |
| `sprig logs` | Print the end of an app's log. |
| `sprig watch` | Follow what workspaces and their apps are doing as it changes. With `--json`, one line per change for other tools; `--usage` adds CPU and memory. |

## Inputs

| Command | What it does |
| --- | --- |
| `sprig inputs` | List each input, its value, and where the value comes from. |
| `sprig set` | Give a workspace's inputs values, kept for next time. |
| `sprig unset` | Clear values set on a workspace, so the default applies again. |
| `sprig link <id> [workspace]` | Have a workspace take its inputs from another workspace's outputs of the same name, such as your branch of a web app against `main` of the API it calls. `--input name=output` points an input at an output of another name. One workspace of each repo at a time: linking to another replaces it. Kept for next time. |
| `sprig unlink [id]` | Stop a workspace taking inputs from a repo's workspace, or from every one, so the defaults apply again. |

## Repos, orchestrations and approval

| Command | What it does |
| --- | --- |
| `sprig repo add \| copy \| ls \| rm` | The Repos list: repo folders that orchestrations use by ID. |
| `sprig orchestration add \| ls \| rm` | Register the orchestration files that put repos together. |
| `sprig trust add \| ls` | Approve definition files to run, by exact content. |

## The app and sprig itself

| Command | What it does |
| --- | --- |
| `sprig open [id] [workspace]` | Launch the desktop app, or bring a window of it to the virtual desktop you're on. Name a workspace to open its page in a window of its own. |
| `sprig update` | Install a newer release in place. `--check` only reports. If sprig is open or apps are running, it asks before quitting them; `--yes` skips the question, and is needed where it can't ask. |
| `sprig host status \| stop` | The background process that runs workspaces' apps. |
| `sprig shell install \| shims` | Shell integration: PATH entries and shims. |

`--json` on a command gives machine-readable output. Secrets are hidden in all output unless you pass `--unmask-secrets`.
