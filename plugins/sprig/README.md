# Sprig — Claude Code plugin

Configure, check out, release and tear down [sprig](https://github.com/ArcticGizmo/sprig) workspaces — isolated git worktrees with non-colliding ports and per-workspace docker infra — from inside Claude Code.

The plugin is a thin driver over the `sprig` CLI. It adds no engine of its own: it calls `sprig … --json --ni` and supplies the judgement (what a repo provides and needs, which workspace to release, when it's safe to destroy one) and the natural-language triggers a raw CLI can't.

## Requirements

- The `sprig` CLI on your PATH, v0.7.0 or later (the map model) — install from https://github.com/ArcticGizmo/sprig.
- git; Docker Desktop for workspaces whose repos declare infra.

## What's in it

### Skills (model-invoked — just describe what you want)

| Skill | Use it for |
|---|---|
| `sprig-configure` | Author/repair a repo's `.sprig.json` (`provides`/`needs`), virtual repos, and maps |
| `sprig-workspace` | Check out, inspect, switch and bring up workspaces; tag the ones you own |
| `sprig-teardown`  | Release, stop or destroy workspaces safely; detect & repair drift (doctor) |

Examples: "set up sprig for this repo", "check out a workspace from web+api for the audit-log ticket", "move safety-3 onto a new branch from main", "which workspace am I in", "I'm done with feature-x", "why won't checkout work — api has no provider".

### Slash commands (deterministic shortcuts)

`/sprig:sprig-status [workspace|map]` · `/sprig:sprig-checkout [map] --branch …` · `/sprig:sprig-release <workspace>` · `/sprig:sprig-up <workspace>` · `/sprig:sprig-down <workspace> [--volumes]` · `/sprig:sprig-rm <workspace>` (routes through the safe teardown skill) · `/sprig:sprig-doctor [workspace] [--repair]`

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

## Working with Claude Code worktrees

`sprig settings set --worktree-layout claude` makes new workspaces nest their worktrees in `<repo>/.claude/worktrees/<workspace>`, where Claude Code keeps its own. A session started in the main repo can then move into a sprig workspace with Claude's worktree tooling, and its history is filed under one path however it was entered. The old `<repo>--<workspace>` path stays as a junction.

## Design rules the components follow

- **Parse, don't scrape.** Always `--json`; check the exit code, then read `error` / `teardownFailed` / `issues[]` / `pending[]` from the payload.
- **Release before destroy.** "I'm done" means `pool release`. `ws rm`, `--force` (delete branch) and `--volumes` (wipe data) only ever run after an explicit user decision surfaced by the teardown skill.
- **Own what you claim.** Workspaces Claude checks out for itself are tagged (`--tag agent.session=<id>`) so they can be found and released later. Sprig never decides a claim is abandoned.
- **Degrade gracefully.** `sprig` missing, Docker down, no maps defined, a full pool — each is a handled state with a next step, not a crash.
- **Driver, not fork.** The plugin never reimplements sprig's allocation/worktree/compose logic; when sprig gains a flag, the plugin inherits it.

## Optional: ambient workspace awareness (opt-in hook)

Not shipped active — it would run on every session start and needs `sprig` on PATH. To enable it, add `hooks/hooks.json` under the plugin with a `SessionStart` hook that runs `sprig here --json` and, when it exits 0, injects a one-line note naming the workspace, its label and its entry URL. Outside a workspace `here` exits 1, so the hook stays silent. Keep the timeout short, guard a missing `sprig` as a no-op, and use `${CLAUDE_PLUGIN_ROOT}` for any bundled script path. See the Claude Code [plugin hooks reference](https://code.claude.com/docs/en/plugins-reference.md) for the exact format.
