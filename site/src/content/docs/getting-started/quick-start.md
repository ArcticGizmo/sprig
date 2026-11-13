---
title: Quick start
description: Make a repo runnable, run it, then run a branch beside it.
---

## 1. Make the repo runnable

In the repo's folder:

```sh
sprig init
```

If the repo has a `sprig.yaml`, sprig uses it. If not, sprig drafts one from the repo's `.csproj` and `package.json` files and keeps it in sprig, so nothing in the repo changes. Add `--in-repo` to write the draft into the folder as `sprig.yaml` instead.

In the app, the same is **Add a repo** on the Home page or the Repos page.

The first time sprig meets a definition someone else wrote, it asks you to approve it before running any of its commands. Definitions you write or edit yourself approve themselves.

## 2. Run it where it's checked out

```sh
sprig up
```

This runs the `main` workspace: the repo where it's already checked out, on whatever branch it's on, with the definition's own values. Sprig reserves the ports, writes the values into the files the apps read, starts the infra, then starts each app after what it depends on.

```sh
sprig ps               # what's running
sprig info             # this workspace, all of it
sprig logs catalog-api # the end of one app's log
sprig stop catalog-api # stop one app, say to run it from your IDE
```

## 3. Run a branch beside it

```sh
sprig up -b feat-x
```

`feat-x` is checked out in a slot of worktrees of its own, with its own ports, data and env files, so it runs alongside `main` without touching it. See [Workspaces](/sprig/guides/workspaces/) for how slots are reused.

## 4. Edit the definition

Every key is in the [definition format](/sprig/reference/definition-format/). In VS Code or any editor with the YAML language server, a `sprig.yaml` gets completion and checking: see [Editor support](/sprig/guides/editor-support/).
