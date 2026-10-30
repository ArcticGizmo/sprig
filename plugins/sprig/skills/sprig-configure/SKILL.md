---
name: sprig-configure
description: >-
  Author or repair sprig configuration — a repo's .sprig.json (modules that provide and need
  capabilities, with env/compose/setup) and the central map that composes repos. Use when the user
  wants to "set up sprig for this repo", "sprig-ify a repo", "add/fix a .sprig.json", "register a
  repo", "try sprig without committing anything", "make a map of these repos", or hits an unmet or
  ambiguous need at checkout. For checking out or inspecting workspaces use sprig-workspace; for
  releasing or destroying them use sprig-teardown.
---

# sprig-configure

Author the two configuration surfaces sprig reads. Repos are **self-describing**: each module declares what it **provides** and what it **needs**, and wiring is derived by capability name.

- **`.sprig.json`** — in the repo (or held by sprig for a virtual repo). Per module: `provides`, `needs`, `env`, `compose`, `setup`.
- **Map** — in the central store, never in a repo. Lists the repos in play and only the **deviations** from automatic wiring: `wiring` (which provider wins when several could) and `defaults` (a value for a need with no provider in the selection).

Read `references/config-cheatsheet.md` for the field-by-field schema and worked examples before authoring — don't reconstruct the schema from memory.

## Integration contract

Calls take `--json --ni`; check the exit code, then parse `{ ok, error }`. Config is **committed to the user's repo**, so **propose-and-confirm** — show the proposed `.sprig.json` and let the user approve before writing.

## A. Author / repair a repo's `.sprig.json`

1. **Get sprig's proposal** (it detects modules, ports, env and compose files):
   ```bash
   sprig init <repo> --print --json
   ```
   `--print` previews without writing. The JSON has the proposed `Config` and any `Notes`.
2. **Reason over it against the real repo:**
   - **Modules.** A single-app repo has one module at the root (or uses top-level sugar). A monorepo lists `modules[]`, each with a `path` (`apps/web`, `apps/api`). Check the split is right.
   - **Provides.** Name each capability after the *service* (`api`, `vite-server`, `db`), not a socket. It owns real `ports` (allocated per workspace) and derived `shapes` (`url`, a connection string) built only from its own outputs. Add `open` for a browsable entry point and `probe` for a health URL.
   - **Needs.** Name the capability another repo (or a sibling module) provides. Use `as` for a local alias. Consume finished values (`${sprig.api.url}`), never another repo's raw port.
   - **env** — each entry targets one `.env.*` file (relative to the module's `path`) and sets keys to `${sprig.<capability>.<output>}` templates. Use `templates` to seed from a committed `.env.template` when the real file is gitignored.
   - **compose** — `overrides[]` are YAML-path replacements (e.g. `["services","postgres","ports","0"] → "${sprig.db.port}:5432"`), plus a `container_name` suffixed with `${sprig.workspace}`. Drop compose files that shouldn't be isolated.
   - **setup** — per-module ordered commands run after checkout (`npm ci`, `dotnet restore`). First non-zero exit stops the rest; `${sprig.*}` is NOT expanded in setup commands.
   - **Pinned ports** — `"ports": { "port": { "allowed": "8100-8103" } }` when only certain ports are valid (Auth0 callbacks).
3. **Show the proposal, get approval, then write:**
   - `sprig init <repo> --force --json` writes `.sprig.json` (overwrites), or hand-edit the file for what detection can't infer.
   - Valid template references: an output of a capability the module provides, any output of a capability it needs, and `${sprig.workspace}`. Anything else is rejected.
4. **Register the repo** so maps can reference it:
   ```bash
   sprig repo add <path> --json              # name defaults to the folder; --name to override
   sprig init <repo> --register --json       # write + register in one step
   sprig repo ls --json
   ```

### Try it without committing (virtual repos)

When the user doesn't want to commit a `.sprig.json` yet, register the repo as **virtual** — sprig holds the config instead:

```bash
sprig repo add <path> --virtual --json                     # starts from the repo's .sprig.json, else a scaffold
sprig repo add <path> --virtual --from <file.json> --json  # starts from a config you wrote
```

One checkout can be registered under several names with different configs (`--name`). The app's **Write to repo** turns a finished virtual config into a committed `.sprig.json`.

## B. Author a map

Maps are authored in the app's **Maps** page, or written as JSON and imported:

```bash
sprig map import <file.json> --json   # validates against the registry, then saves
sprig map show <name> --json
sprig map ls --json
```

Write the file somewhere temporary (not in a repo). Minimal shape:

```json
{ "schema": 1, "name": "web+api", "repos": ["vue-web", "postgres-api"], "maxSlots": 4 }
```

- **Start with just `repos`.** Most maps need nothing else: each need is met by the one selected repo that provides it, or by a sibling module in the same repo.
- **`wiring`** only when two or more selected repos provide the same capability: `wiring[repo][capability] = providerCapability`.
- **`defaults`** only for a need whose provider isn't in the map: `defaults[repo][capability][output] = literal` (e.g. a shared staging URL).
- **`maxSlots`** caps the pool (default 5).
- A repo entry can be `{ "name": "billing", "repo": "<git-url>" }` so a shared map clones repos a machine doesn't have.

## C. Fix a checkout that failed on wiring

- **Unmet need** (`repo.module needs '<capability>'`): nothing selected provides it. Either add the providing repo to the map, add a `defaults` entry with a literal value, or fix a typo in the capability name on either side.
- **Ambiguous need** (more than one provider): add a `wiring` entry naming the winner.

Re-import the map, then hand back to `sprig-workspace` to retry the checkout.

## D. Machine settings (optional)

```bash
sprig settings show --json
sprig settings set --start 8000 --end 9000 --restrict 8080,8443 --json
sprig settings set --worktree-layout claude --json   # nest worktrees in .claude/worktrees (new workspaces only)
```

The worktree layout is a personal, machine-wide choice — only change it when the user asks.

## Common shapes (see references/config-cheatsheet.md for full JSON)

- **Provider with infra** — provides `api` (port + `url` shape) and `db` (port); owns the compose file.
- **Consumer, no infra** — provides its own `web` port; needs `api` and reads `${sprig.api.url}`.
- **Monorepo** — `api` and `web` modules in one repo; the sibling need wires locally, no map entry needed.
- **Auth0 front end** — a provided port with `allowed: "8100-8103"`.
