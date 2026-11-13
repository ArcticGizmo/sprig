---
title: Workspaces
description: main, slots, and what happens to a workspace's data.
---

A workspace is one running copy of a repo (or of an orchestration's repos): its checkout, its ports, the values it wrote into files, and its infra's data.

## main

Every repo has a `main` workspace, listed first. It's the repo where it's already checked out, on whatever branch it's on, run with the definition's own values. There's nothing to create, free or remove.

`sprig up` with no `-b` runs it.

## Slots

`sprig up -b feat-x` checks `feat-x` out in a numbered slot of worktrees, such as `shop-web--1`. The branch is the slot's only name, so git, your IDE and sprig all call it the same thing. Commands name a workspace by its branch, its slot number or its folder:

```sh
sprig logs feat-x web
sprig info feat-x
```

From inside a slot's worktree, sprig works out which workspace you mean, so `sprig logs web` is enough.

## Freeing and reusing a slot

```sh
sprig release feat-x
```

Releasing stops the slot's apps and infra and detaches its worktrees. The branch and its commits stay, and so do the slot's folders, installed packages, ports, values and data, ready for the next branch to reuse without a reinstall. Checking a branch out takes the lowest free slot.

## Stopping, emptying and removing

| Command | What it does |
| --- | --- |
| `sprig stop` | Stops the apps. Infra keeps running. |
| `sprig down` | Stops the infra too, keeping its data, worktrees and ports. |
| `sprig empty-data` | Deletes the containers' volumes, then brings the workspace back as it was. Jobs such as migrations run again against the empty database. Works on `main` too. |
| `sprig rm` | Removes the workspace: its containers and their data, its worktrees, and the values it wrote. |

## Groups

A definition's `groups` name a slice of its apps:

```yaml
groups:
  api: [catalog-api]
```

`sprig up --group api` starts that slice, what its apps wait for, and only the infra they use. Starting another group on a workspace that's already up adds to what's running rather than starting over.

## Inputs

An input is a value a repo takes from outside, with a default for when nothing gives one. A value set on the workspace wins over the default:

```sh
sprig up --set catalog-url=http://localhost:5100
sprig set catalog-url=http://localhost:5100
sprig inputs           # each input, its value, and where it came from
```

An input with no default is asked for when the workspace is made.
