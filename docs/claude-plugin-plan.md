# Sprig Claude Code plugin — plan

A plan for a **Claude Code plugin** that helps developers *configure*, *create*, and *tear down*
sprig workspaces from inside an agentic coding session, driving the existing `sprig` CLI.

> Status: planning. Nothing here is built yet. Plugin mechanics below are confirmed against the
> current Claude Code plugin docs (linked at the end); the sprig integration contract is settled.
>
> One thing the docs make explicit and that shapes this plan: **flat `commands/*.md` slash commands
> are now the legacy surface — `skills/` is the preferred layout for new plugins.** That's why the
> recommendation leads with skills and treats commands as optional sugar.

## Why a plugin (and why now)

The `sprig` CLI is already a first-class, scriptable surface: a global `--json` flag is an explicit
stability contract, `--ni` forces the non-interactive path, and every command exits `1` with
`{ ok: false, error }` on failure. That is exactly the shape an agent wants — sprig does the work,
the plugin supplies the *judgement* (what modules a repo has, which ports to wire, when it's safe to
destroy a workspace) and the *natural-language triggers* that a raw CLI can't.

So the plugin is thin: **no new engine, no MCP server required.** It's a bundle of skills + a few
slash commands that shell out to `sprig … --json --ni` and reason over the results.

## The integration contract (the one rule every component follows)

Every non-interactive sprig call the plugin makes uses:

```
sprig <command> … --json --ni
```

- `--json` → machine-readable stdout (a stability contract, safe to parse).
- `--ni` → never prompt; fail loudly instead of hanging waiting for a TTY that an agent can't answer.
- Check the **exit code first**; on `1`, parse `{ ok:false, error }` (or a `teardownFailed` payload)
  and surface the message rather than retrying blindly.
- Destructive verbs need their explicit gate: `ws rm` requires `--yes` (and `--force` to also delete
  the branch); `down` takes `--volumes` to wipe data. The plugin never passes these without an
  explicit user decision.

Read-only commands the plugin leans on for state: `sprig ws ls --json`, `sprig ws info <w> --json`
(record + drift + live containers in one shot), `sprig ws reconcile --json`, `sprig repo ls --json`,
`sprig stack ls/show --json`, `sprig settings show --json`.

## Command surface the plugin maps onto

| Lifecycle | sprig commands the plugin uses |
|---|---|
| **Configure** | `sprig init [repo] --print/--force/--register`, `sprig repo add/ls/rm`, `sprig stack create/edit/ls/show/rm/export/import`, `sprig settings show/set` |
| **Create** | `sprig create <name> --stack <s> [--only/--without] [--skip-infra]` (alias `sprig ws create`), `sprig ws up/reset`, `sprig ws ls/info/status` |
| **Tear down** | `sprig ws down [--volumes]`, `sprig ws rm <w> --yes [--force]`, `sprig ws reconcile/doctor [--repair]` |

Two config surfaces the *configure* skill must understand (from `docs/config-reference.md`):
- **`.sprig.json`** (schema 3) committed in a repo — `inputs[]` (shared across modules) and
  `modules[]`, each with `path` + `env[]` + `compose[]` + `setup[]`. Consumer only.
- **Stack** (schema 2) in the central store — `repos[]`, named `ports[]`, and
  `bindings[repo][input] = expression` over `${sprig.ports.<name>}` / literals. Producer.
  Data flows one way: **stack → repo**.

---

## Recommended shape: 3 skills + thin commands + optional hook

Skills carry the reasoning and the natural-language triggers; slash commands are deterministic
shortcuts for the common verbs; one optional hook adds ambient awareness. Recommendation is to ship
the three skills first (they're the value), add commands as sugar, and treat the hook as opt-in.

### Skill 1 — `sprig-configure` (the high-value one)

**Triggers:** "set up sprig for this repo", "sprig-ify this repo", "add a .sprig.json", "wire these
repos into a stack", "why won't my workspace create — an input is unbound".

**What it does — repo config:**
1. Run `sprig init <repo> --print --json` to get sprig's *proposed* `.sprig.json` (it autodetects
   modules, ports, env files, compose files) plus its `notes`.
2. Reason over the proposal against the real repo: is this a monorepo (multiple `modules[]`, each with
   a `path`)? Are the detected `inputs[]` named sensibly? Do env `set` templates reference the right
   `${sprig.<input>}`? Are `compose[].overrides[]` pointed at the right YAML paths? Are `setup[]`
   commands (e.g. `npm ci`, `dotnet restore`) present and per-module?
3. Present the diff, then write it — `sprig init --force` or hand-edit `.sprig.json` — and validate by
   re-loading (`sprig repo add` / a create dry-run surfaces validator errors).
4. Register: `sprig repo add <path> [--name]` (or `sprig init --register`).

**What it does — stack authoring/repair:** given N registered repos, propose a stack: which named
`ports[]` to declare, and the `bindings[repo][input]` that wire each repo's inputs — including the
**shared-port** pattern (front-end `apiUrl` → same `${sprig.ports.api_port}` the API's `port` binds
to). Emit as `sprig stack create <name> --repos … --port … --bind repo:input=expr …`, then
`sprig stack show <name>` to confirm. Knows the gotchas: every declared input **must** be bound
(unbound = hard failure at create), `allowedPorts` pins an input to a fixed port set, and same-named
inputs across repos are independent.

**Reference material bundled with the skill:** a trimmed copy of `docs/config-reference.md` and 2–3
worked `.sprig.json` + stack examples (single-app, monorepo, shared-port) so the skill authors idiomatic
config without re-deriving the schema each time.

### Skill 2 — `sprig-workspace` (create + inspect + bring up)

**Triggers:** "spin up a workspace for feature-x", "create an isolated env from the web+api stack",
"bring feature-x's infra up", "what workspaces are running", "show me feature-x".

**What it does:**
1. Discover options: `sprig stack ls --json`, `sprig repo ls --json`. If no stack fits, hand off to
   `sprig-configure`.
2. Create: `sprig create <name> --stack <s> --json --ni`, honouring **partial** selection
   (`--only a,b` / `--without c`) when the user only needs some repos, and `--skip-infra` when they
   don't want Docker started yet (infra starts by default otherwise).
3. Report the allocated ports + worktree paths from the JSON record; call out a **soft setup failure**
   (`record.repos[].setup[].success == false`) — the workspace is kept, setup is finished by hand.
4. Lifecycle & inspection: `sprig ws up/reset`, `sprig ws ls`, `sprig ws info <w>` (folds in drift +
   live containers), `sprig ws status`.
5. Jump-in guidance: point the user at `sprig cd <w>` (new terminal in the worktree) / `sprig path <w>`
   for scripts — **not** something the agent runs itself.

### Skill 3 — `sprig-teardown` (safe destruction + drift repair)

**Triggers:** "tear down feature-x", "stop feature-x's infra", "clean up old workspaces", "sprig says
teardown failed", "fix workspace drift", "run doctor".

**What it does:**
1. **Always confirm before destroying.** Show what dies (worktrees removed, volumes wiped, infra
   stopped) and ask whether to also delete the branch (`--force`, "loses any commits made in the
   worktree") before running anything.
2. Stop-only vs destroy: `sprig ws down <w>` (keep, optionally `--volumes` to wipe data) vs
   `sprig ws rm <w> --yes [--force]` (full teardown). Never passes `--yes`/`--force`/`--volumes`
   without an explicit answer.
3. **Handle teardown-failed:** `rm` keeps a flagged record and exits `1` with `issues[]` when a layer
   couldn't be removed. The skill surfaces the issues, explains teardown is **idempotent**, and
   offers to retry after the blocker is fixed.
4. **Drift / doctor:** `sprig ws reconcile --json` (all or one) to detect record-vs-reality drift (a
   deleted or orphaned worktree); offer `--repair`. Good "clean up my machine" entry point.
5. Batch cleanup: enumerate `sprig ws ls --json`, propose which to remove (e.g. stale / teardown-failed),
   confirm the set, then remove one by one — reporting each result.

### Thin slash commands (deterministic sugar)

For users who'd rather type a verb than a sentence. Each is a small markdown command that runs the
mapped `sprig … --json` call and formats the result:

- `/sprig-status` → `sprig ws ls --json` (+ optional per-workspace `info`).
- `/sprig-create <name> [--stack s]` → forwards args to `sprig create --json --ni`.
- `/sprig-up <w>` / `/sprig-down <w>` → infra up / down.
- `/sprig-rm <w>` → **invokes the `sprig-teardown` skill** (so the confirm/branch-delete reasoning
  runs) rather than shelling straight to `rm --yes`.
- `/sprig-doctor [w]` → `sprig ws reconcile [--repair] --json`.

`argument-hint` on each advertises the expected args; `allowed-tools` scopes them to `Bash(sprig:*)`.

### Optional hook — ambient workspace awareness

A `SessionStart` hook that runs `sprig ws ls --json` and injects a one-line summary ("3 sprig
workspaces; 1 teardown-failed") so the agent knows the lay of the land without being asked. Keep it
**opt-in** and fast (single read-only call, short timeout) — and guard for `sprig` not being on PATH
so the hook is a silent no-op on machines without it. A `PreToolUse` guard that intercepts a raw
`sprig ws rm` without `--yes` is possible but redundant given the CLI already refuses it; skip unless
users bypass the teardown skill.

---

## Plugin package layout

```
sprig-plugin/                     # in this repo under plugins/sprig/, or its own repo
├── .claude-plugin/
│   ├── plugin.json               # manifest
│   └── marketplace.json          # if hosting the marketplace from here
├── skills/
│   ├── sprig-configure/
│   │   ├── SKILL.md
│   │   └── references/           # trimmed config-reference + worked examples
│   ├── sprig-workspace/SKILL.md
│   └── sprig-teardown/SKILL.md
├── commands/                     # optional sugar (legacy surface; skills preferred)
│   ├── sprig-status.md
│   ├── sprig-create.md
│   ├── sprig-up.md
│   ├── sprig-down.md
│   ├── sprig-rm.md
│   └── sprig-doctor.md
└── hooks/
    └── hooks.json                # optional SessionStart summary
```

`.claude-plugin/plugin.json`:

```json
{
  "name": "sprig",
  "displayName": "Sprig",
  "description": "Configure, create and tear down sprig workspaces from Claude Code",
  "version": "0.1.0",
  "author": { "name": "ArcticGizmo", "url": "https://github.com/ArcticGizmo/sprig" }
}
```

`SKILL.md` frontmatter is just `name` (optional; defaults to the folder) + `description` (required —
this is what triggers the skill). Skills are invoked as `/sprig:sprig-configure` etc. Use
`${CLAUDE_PLUGIN_ROOT}` for any bundled path a command/hook references (quote it in shell:
`"${CLAUDE_PLUGIN_ROOT}/…"`), and `${CLAUDE_PLUGIN_DATA}` for any persistent state.

**Distribution.** Publish `.claude-plugin/marketplace.json` (`name`, `owner`, `plugins[] { name,
source }`; `source` a relative path like `"./plugins/sprig"` when hosted in this repo). Teammates then:

```bash
/plugin marketplace add ArcticGizmo/sprig     # GitHub shorthand
/plugin install sprig@sprig                    # <plugin>@<marketplace>
```

**Local dev/test** before publishing: `claude --plugin-dir ./plugins/sprig` (and
`claude plugin init sprig --with skills hooks` to scaffold the tree).

## Cross-cutting design rules

- **Never run destructive verbs unprompted.** `ws rm`, `--force`, `--volumes` always follow an explicit
  user decision surfaced by the teardown skill.
- **Parse, don't scrape.** Always `--json`; branch on exit code; read `error` / `teardownFailed` /
  `issues[]` from the payload. Human-formatted output is for humans, not the agent.
- **Degrade gracefully.** `sprig` missing from PATH, Docker not running (info/status degrade to
  "docker unavailable"), no stacks defined — each is a known, handled state with a next step, not a
  crash.
- **Windows-first, OS-agnostic core.** Commands run through the shell; keep them single commands
  (sprig's own `setup[]` guidance warns `&&`-chaining is finicky on Windows `cmd`).
- **Stay a driver, not a fork.** The plugin never reimplements allocation, worktree, or compose logic
  — it calls sprig and reasons over the result. When sprig gains a flag, the plugin inherits it.

## Suggested build order

1. **`sprig-workspace` + `sprig-teardown` skills** — the create/inspect/destroy loop is the daily
   driver and the fastest thing to make useful.
2. **Thin slash commands** wrapping the read-only + up/down verbs.
3. **`sprig-configure` skill** with bundled reference material — the highest-reasoning piece; worth
   doing once the mechanical loop is proven.
4. **Optional `SessionStart` hook.**
5. **`marketplace.json`** + a short install section in the README.

## Open questions for the maintainer

- Ship the plugin **inside this repo** (under `plugins/sprig/`, dogfooded via a repo-local
  marketplace) or as its **own repo**? In-repo keeps it versioned with the CLI it drives.
- Is a `sprig cd`/`sprig path` "jump in" worth a command, or left as guidance? (Memory notes the
  cd-in-place wrapper is a settled "won't do".)
- Should `sprig-configure` ever **write** `.sprig.json` autonomously, or always propose-and-confirm?
  (Recommendation: propose-and-confirm — config is committed to the user's repo.)

## References

- Plugins reference (manifest, skills, commands, hooks, MCP): https://code.claude.com/docs/en/plugins-reference.md
- Creating plugins: https://code.claude.com/docs/en/plugins.md
- Marketplaces: https://code.claude.com/docs/en/plugin-marketplaces.md
- Sprig config surfaces: [`docs/config-reference.md`](config-reference.md); CLI in `src/Sprig.Cli/`.
