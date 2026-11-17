# Agents use sprig: plan

Branch: `agent-support`. Written 2026-11-17.

## Why

The user: "how we best make it so that sprig can best be used by AI agents to assist in making process ready for sprig, and be able to create and run worktrees effectively." Then, on shipping it as Claude Code plugins: "It is also important that the plugins/hooks are as modular as possible as I want people to use composition over configuration so they can take the parts they like and ignore the others."

The CLI is already most of the way there: `--json` is a stability contract, prompts never show under `--json` or when input is piped, every prompt has a flag that answers it, and a refusal names the flag to use next. What's missing is a tight loop for writing a definition, a way to teach an agent the workflow, and a way for an agent's own worktree to be a sprig slot.

## Decisions (user, 2026-11-17)

1. **The CLI is the interface.** No MCP server: `--json` is the contract sprig already keeps, and a second surface would be a second thing to keep stable.
2. **Plugins live in this repo**, under `plugins/`, with the marketplace at the root (`.claude-plugin/marketplace.json`). Users add it with `/plugin marketplace add arcticgizmo/sprig`.
3. **Composition over configuration.** Each part is its own plugin, installable alone, depending on no other and taking no options. Want the skills but not the worktree hooks? Install the one, not the other.
4. **Hooks are thin; the binary owns the behaviour.** A hook is one line, `sprig hook <name> --contract <n>`. What it reads, writes and does is in sprig, versioned and tested with everything else, so a plugin rarely has to change.
5. **A contract number, not a version match.** Sprig and the plugins update separately (`sprig update` and Claude Code's `/plugin`, whose auto-update is off by default for marketplaces other than Anthropic's). A hook passes the contract it was written for; sprig serves the contracts it knows and refuses others, saying which side to update. The contract changes only when a hook's input or output changes in a way an older side would misread.
6. **Plugins carry sprig's version.** `version` in each `plugin.json` is the release's, bumped by `bump-version` with the csproj, and a test keeps them equal. Without one, every commit to main would count as a plugin update, ahead of the released binary.

## Design

### The plugins

| Plugin | What it adds | Hooks |
|---|---|---|
| `sprig-onboard` | Skill: make a repo ready for sprig (draft, check, plan, start, read what failed, edit) | none |
| `sprig-workspaces` | Skill: run workspaces (up, start, logs, stop one and run it yourself, free) | none |
| `sprig-worktrees` | An agent's worktree is a sprig slot: ports, files and infra of its own | `WorktreeCreate`, `WorktreeRemove` |
| `sprig-context` | Each session starts knowing what this folder is to sprig, and whether sprig is installed and new enough | `SessionStart` |

Skills don't copy the docs: they tell the agent to run `sprig docs format` and `sprig docs agent`, which print the ones that match the installed sprig.

### The CLI

- **`sprig check [file]`**: reads a definition without registering or approving it (reading runs nothing) and plans it, so references, ports and files are checked too. Every problem has its file and line; `--json` gives `{ ok, code, file, kind, problems, asks }`, with each problem as `plan --json` gives it (`Severity`, `Message`, `Location: { File, Line }`). A repo file is planned on its own, as a workspace that isn't main; an orchestration file with the repos it names.
- **`sprig schema [repo|orchestration]`**: prints the JSON Schema, the same one the site publishes.
- **`sprig docs [format|agent]`**: prints the definition format, or a short guide for agents, embedded in the binary.
- **Error codes under `--json`.** A failure is `{ ok: false, code, error, … }`, with `code` one of `inputs-needed` (with `asks: [{ name, type, owner }]`), `untrusted` (with `untrusted: [path]`), `held` (another definition's main is running in the checkout), `slot-broken`, `problems` (with `problems`) or `error`. An agent branches on `code`, not the English.
- **`sprig hook <name> --contract <n>`**: what the plugins' hooks run. Contract 1:
  - `worktree-create` reads Claude Code's `{ cwd, name }` on stdin. In a repo or workspace sprig knows, it checks branch `<name>` out in a slot (as `sprig up -b <name> --no-start`, approving definitions the user's own edits already approve, and no others) and prints the repo's worktree folder. Anywhere else it does what Claude Code would have: `git worktree add` under `.claude/worktrees/<name>` on a new branch `worktree-<name>`. Everything but the path goes to stderr.
  - `worktree-remove` reads `{ worktree_path }`. A slot's worktree: the slot is freed, keeping its folder for the next branch (exit 0, so Claude Code takes it as handled). A plain worktree it made: `git worktree remove`.
  - `session-start` prints a line or two of context: the repo or workspace this folder is, its apps and their urls, or that this isn't a sprig repo. It exits 0 either way, so a session never fails to start over it.
  - Any other contract: exit 2, with "this plugin needs a newer sprig: run sprig update" or "update the plugin: /plugin" on stderr.

### Catches

- A `WorktreeCreate` hook replaces `git worktree add` for every worktree while the plugin is on, which is why `worktree-create` falls back to a plain worktree, and why `sprig-worktrees` is best turned on per project (`enabledPlugins` in the repo's `.claude/settings.json`).
- Subagent and workflow worktrees never fire `WorktreeRemove`, so slots made for them stay taken. Slots made by the hook are marked, so a later `sprig free --agent-made` can sweep them.
- Claude Code's own `.worktreeinclude` isn't processed when a hook makes the worktree; sprig's `copy-into-worktrees` does that job.
- The plugin hook doesn't run for `claude --worktree --tmux` (the worktree is made before plugins load); that needs the hook in a settings file.

## As built (2026-11-17)

- **CLI**: `CheckCommand`, `SchemaCommand`, `DocsCommand` and `HookCommand` (`AgentCommands.cs`, `HookCommand.cs`). `check` reads with a trust store that approves everything and records nothing, since reading runs nothing; a repo file is planned as a repo on its own, keyed by its name. `docs` prints `docs/definition-format.md` and `docs/agent-guide.md`, embedded in `sprig.exe`. `CliErrors` gives every `--json` failure its `code`; `WorkspaceException` gained `Code` (for `slot-broken`) and the non-interactive take-over refusal keeps `HeldBy`. `ProblemSeverity` serialises by name.
- **Hooks**: `worktree-create` names the branch `worktree-<name>`, as Claude Code does, checks it out with `CheckOut` and no `Starting`, records `MadeForAgent` (the session id) on the slot, and prints the worktree of the repo the session was in. Definitions the user hasn't approved fail the hook with the `sprig trust add` line rather than fall back, so an agent never gets a worktree without the ports it expected. The plain fallback puts the worktree under the main checkout's `.claude/worktrees/`; removing it deletes its branch only when no other branch or remote has its commits. `session-start` prints nothing outside a repo sprig knows, and never fails.
- **Plugins**: `plugins/sprig-onboard`, `sprig-workspaces` (one skill each), `sprig-worktrees`, `sprig-context` (hooks in exec form, `"command": "sprig", "args": [...]`); `.claude-plugin/marketplace.json` lists them by relative path. All pass `claude plugin validate --strict`; the only advice is that none has an icon, which matters only for Anthropic's plugin directory.
- **Alignment**: `PluginsTests` checks every plugin's version equals the csproj's, every hook runs `sprig hook` with a contract `HookCommand.Contracts` serves, and the marketplace lists each plugin folder once. `bump-version` bumps the plugin manifests with the csproj.
- **Tests**: check (good file, reference typo by line, unknown key, missing file), docs, schema, error codes, the contract refusal, both worktree paths (plain and slot, create and remove, approval required) and session context.

## Follow-ups built (2026-11-17)

The user: "Continue with the not done section."

- **Slot claims.** Choosing a slot and claiming it happen together under a lock (`WorkspaceStore.ClaimSlot`, `slot-claims.json`), held until `CheckOut` or `MakeFreeSlot` finishes. `SlotFor`, `FreeSlotName` and `NextUsableSlot` skip claimed slots; a branch or slot another check-out holds is refused with `slot-busy`. A claim records its process id and start time, so one left by a crash holds nothing. `FileLock` moved to `Store/` and is shared with `PortLeases`, which already locked its leases.
- **`sprig free --agent-made`** frees every slot with `MadeForAgent` that's still taken (one ID's, when named), leaving those with something running unless `--running`.
- **Why a start failed.** `start --json` and `up --json` add `code` (`start-failed`, or `start-timed-out` when some were still starting after five minutes), `error`, and `failed`: per app its `State`, `Why`, `ReadyWhen` (the health check or log line it needed, or "it finishes with exit code 0" for a job) and the last 30 `LastLines` of its log. `start --json` prints the JSON error when making the workspace ready fails, where it printed only to stderr. `RunnerState` serialises by name in the CLI's JSON (the host protocol's own options still use camelCase).
- **CI**: a `plugins` job in the Tests workflow installs a pinned Claude Code and runs `claude plugin validate --strict` on the marketplace and each plugin.
- **Icons**: each plugin carries `landing-icon.png` (512×512) as `.claude-plugin/icon.png`, which clears the validator's only advice.
- **`sprig` on PATH**: every route Claude Code might take to `sprig` ends at `sprig.exe` (a direct spawn finds `current\sprig.exe`; the cmd, PowerShell and Git Bash shims all run it with stdin and stdout passed through). Not yet tried in a real Claude Code session.

## Order

1. `sprig schema`, `sprig docs`, `sprig check`.
2. Error codes under `--json`.
3. `sprig hook` (contract 1).
4. The four plugins, the marketplace, and the version test; `bump-version` bumps the plugins.
5. `sprig free --agent-made`; why a start failed in `start --json`; two agents can't take the same slot or port at once; plugins validated in CI. (Built; see Follow-ups.)
6. Left: try the plugins in a real Claude Code session.
