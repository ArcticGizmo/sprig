---
name: sprig-onboard
description: Make a repo ready for sprig by writing and testing its sprig.yaml, so its apps and infra start in an isolated worktree slot with their own ports and env files. Use when the user asks to set a repo up for sprig, write or fix a sprig.yaml, onboard a repo to sprig, or when sprig check or sprig start reports problems with a definition.
---

# Make a repo ready for sprig

Sprig runs a repo's apps and infra from a `sprig.yaml` at its root. Your job is a definition that passes `sprig check` and whose apps start in a slot.

## Before anything

1. Run `sprig docs agent`. It is the guide for the sprig installed here, so follow it over anything you remember. If `sprig` isn't found, tell the user to install it (https://arcticgizmo.github.io/sprig/getting-started/install/) and stop. If `docs` isn't a command, sprig is too old: tell the user to run `sprig update`.
2. Run `sprig docs format` and read it whole before writing any YAML. The format is strict: unknown keys are errors, and `${…}` goes only in values, never in commands.

## The loop

1. **Draft.** In the repo, `sprig init --in-repo --json` drafts a `sprig.yaml` from its projects, or uses one that's already there.
2. **Find what the draft can't know.** For each app, read the repo's own config to see where it reads its port, urls and connection strings (`launchSettings.json`, `appsettings*.json`, `.env`, `vite.config.*`). That file goes under `files:` with the values sprig should write into it. Find what each app needs running first (`wait-for`), and jobs such as migrations (`once: true`).
3. **Check.** `sprig check --json`. Fix every problem by its file and line, and run it again until `ok` is true. It reads and plans the file; nothing runs and nothing needs approving.
4. **Plan.** `sprig plan --json` shows every port and every value each file gets. Check each app gets what its config expects.
5. **Try it in a slot**, never the user's checkout: `sprig checkout -b sprig-onboard-try --json --trust`, then `sprig up sprig-onboard-try --json`. Pass `--trust` only for a definition you wrote. On failure, branch on `code` in the JSON as `sprig docs agent` describes.
6. **Fix what doesn't start.** `sprig info sprig-onboard-try --json` shows each app's state; `sprig logs <app> -n 100` shows why. Fix the definition or the app's config, then `sprig start sprig-onboard-try --json` again.
7. **Clean up.** `sprig free` gives the slot back. Tell the user what you set up and what you couldn't, and leave committing `sprig.yaml` to them.

## Don't

- Don't put secrets in `sprig.yaml`. Declare an input with `type: secret` and let the user give it a value.
- Don't approve a definition you didn't write (`sprig trust add`, `--trust`) without asking.
- Don't `sprig rm` anything you didn't make.
