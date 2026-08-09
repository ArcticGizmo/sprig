# Sprig — Claude Code plugin

Configure, create and tear down [sprig](https://github.com/ArcticGizmo/sprig) workspaces — isolated
git worktrees with non-colliding ports and per-workspace docker infra — from inside Claude Code.

The plugin is a thin driver over the `sprig` CLI. It adds no engine of its own: it calls
`sprig … --json --ni` and supplies the judgement (which modules a repo has, which ports to wire, when
it's safe to destroy a workspace) and the natural-language triggers a raw CLI can't.

## Requirements

- The `sprig` CLI on your PATH — install from https://github.com/ArcticGizmo/sprig.
- git; Docker Desktop for workspaces whose repos declare infra.

## What's in it

### Skills (model-invoked — just describe what you want)

| Skill | Use it for |
|---|---|
| `sprig-configure` | Author/repair a repo's `.sprig.json` and the stack that wires repos together |
| `sprig-workspace` | Create, inspect and bring up workspaces |
| `sprig-teardown`  | Safely stop/destroy workspaces; detect & repair drift (doctor) |

Examples: "set up sprig for this repo", "spin up a workspace from the web+api stack", "tear down
feature-x and drop its branch", "why won't my workspace create — an input is unbound".

### Slash commands (deterministic shortcuts)

`/sprig:sprig-status [workspace]` · `/sprig:sprig-create <name> [--stack …]` ·
`/sprig:sprig-up <workspace>` · `/sprig:sprig-down <workspace> [--volumes]` ·
`/sprig:sprig-rm <workspace>` (routes through the safe teardown skill) ·
`/sprig:sprig-doctor [workspace] [--repair]`

## Install

```bash
/plugin marketplace add ArcticGizmo/sprig
/plugin install sprig@sprig
```

## Local development

From the sprig repo root:

```bash
claude --plugin-dir ./plugins/sprig      # load the plugin from disk for this session
```

## Design rules the components follow

- **Parse, don't scrape.** Always `--json`; check the exit code, then read `error` /
  `teardownFailed` / `issues[]` from the payload.
- **Never destroy unprompted.** `sprig ws rm`, `--force` (delete branch), `--volumes` (wipe data) only
  ever run after an explicit user decision surfaced by the teardown skill.
- **Degrade gracefully.** `sprig` missing, Docker down, no stacks defined — each is a handled state
  with a next step, not a crash.
- **Driver, not fork.** The plugin never reimplements sprig's allocation/worktree/compose logic; when
  sprig gains a flag, the plugin inherits it.

## Optional: ambient workspace awareness (opt-in hook)

Not shipped active — it would run on every session start and needs `sprig` on PATH. To enable it,
add `hooks/hooks.json` under the plugin with a `SessionStart` hook that injects a one-line summary,
e.g. running `sprig ws ls --json` and emitting a short note ("3 sprig workspaces; 1 teardown-failed").
Guard it so a missing `sprig` is a silent no-op, and keep the timeout short. See the Claude Code
[plugin hooks reference](https://code.claude.com/docs/en/plugins-reference.md) for the exact format
and use `${CLAUDE_PLUGIN_ROOT}` for any bundled script path.
