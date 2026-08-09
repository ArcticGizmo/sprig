---
name: sprig-configure
description: >-
  Author or repair sprig configuration — a repo's committed .sprig.json (inputs + modules with
  env/compose/setup) and the central stack that wires repos together (ports + bindings). Use when the
  user wants to "set up sprig for this repo", "sprig-ify a repo", "add/fix a .sprig.json", "register a
  repo", "wire repos into a stack", "add a shared port", or hits "input is unbound" at create time.
  For creating/inspecting workspaces use sprig-workspace; for destroying them use sprig-teardown.
---

# sprig-configure

Author the two configuration surfaces sprig reads. Data flows **one way: stack → repo**.

- **`.sprig.json`** — committed in a repo. A pure *consumer*: declares `inputs[]` it needs and
  `modules[]` (each with `path` + `env`/`compose`/`setup`). See `references/config-cheatsheet.md`.
- **Stack** — in the central store, never in a repo. The *producer*: lists `repos[]`, owns named
  `ports[]`, and supplies every input via `bindings[repo][input] = expression`.

Read `references/config-cheatsheet.md` for the full field-by-field schema and worked examples before
authoring — don't reconstruct the schema from memory.

## Integration contract

Non-interactive calls take `--json --ni`; check the exit code, then parse `{ ok, error }`. Config is
**committed to the user's repo**, so **propose-and-confirm** — show the proposed `.sprig.json` and let
the user approve before writing.

## A. Author / repair a repo's `.sprig.json`

1. **Get sprig's proposal** (it autodetects modules, ports, env files, compose files):
   ```bash
   sprig init <repo> --print --json
   ```
   `--print` previews without writing. The JSON has the proposed `config` and any `notes`.
2. **Reason over it against the real repo:**
   - **Monorepo?** One repo can declare several `modules[]`, each a slice (`apps/web`, `apps/api`)
     with its own `path`. A single-app repo has one module at the repo root. Check the proposal split
     the repo correctly.
   - **Inputs** are declared **once at the repo level** and shared across all modules — referenced as
     `${sprig.<input>}`. Give them clear names (`apiPort`, `dbPort`, `apiUrl`) and an `example`.
   - **env** — each `env[]` entry targets one `.env.*` file (relative to the module's `path`) and sets
     keys to `${sprig.…}` templates. Use `templates` to seed from a committed `.env.template` when the
     real file is gitignored.
   - **compose** — `overrides[]` are YAML-path replacements (e.g.
     `["services","postgres","ports","0"] → "${sprig.dbPort}:5432"`). Drop compose files that
     shouldn't be isolated.
   - **setup** — per-module ordered commands run after create (`npm ci`, `dotnet restore`). First
     non-zero exit stops the rest; `${sprig.*}` is NOT expanded in setup commands.
   - **allowedPorts** — pin an input to a fixed port set (e.g. Auth0 callback ports `"8100-8103"`)
     when only certain ports are valid.
3. **Show the diff, get approval, then write:**
   - `sprig init <repo> --force` writes `.sprig.json` (overwrites), or hand-edit the file for changes
     sprig's autodetect can't infer.
   - Only the `${sprig.<input>}` / `${sprig.workspace}` tokens are valid in templates — anything else
     is rejected by the validator.
4. **Register the repo** so stacks can reference it:
   ```bash
   sprig repo add <path> --json            # name defaults to the folder; --name to override
   sprig init <repo> --register            # write + register in one step
   sprig repo ls --json
   ```

## B. Author / repair a stack

A stack composes registered repos, owns named ports, and binds every input each repo declares.

```bash
sprig stack create <name> \
  --repos web,api \
  --port frontend_port --port api_port --port db_port \
  --bind web:frontend=${sprig.ports.frontend_port} \
  --bind web:apiUrl=http://localhost:${sprig.ports.api_port} \
  --bind api:port=${sprig.ports.api_port} \
  --bind api:dbPort=${sprig.ports.db_port} \
  --json
```

Then verify with `sprig stack show <name> --json`.

Key rules:
- **Every declared input MUST be bound** — an unbound input is a hard failure at *create* time (names
  the repo, input, and example). Bind all of them.
- A binding is either a **literal** (`http://localhost:4000`) or a **template** over
  `${sprig.ports.<name>}` / `${sprig.workspace}`.
- **Shared port** = the way two repos talk to each other: point both bindings at the *same*
  `${sprig.ports.<name>}` (above, the web app's `apiUrl` and the API's `port` share `api_port`, so the
  frontend hits *its own* isolated API). sprig records this in `shares` automatically.
- Same-named inputs in **different** repos are independent — bind each one.

Edit an existing stack (each facet replaced only if its flag is given; bindings merge):
```bash
sprig stack edit <name> --bind api:dbPort=${sprig.ports.db_port} --json
```
Other stack verbs: `ls`, `show`, `rm`, `export <name> <file>`, `import <file>`.

## C. Fix an "input is unbound" create failure

When `sprig ws create` fails with an unbound-input error: the repo declares an input the stack doesn't
bind. Add the binding with `sprig stack edit <stack> --bind <repo>:<input>=<expr>`, confirm with
`sprig stack show`, then retry the create (hand back to `sprig-workspace`).

## D. Port policy (optional)

Allocation draws from a configurable range (default `8000–8999`):
```bash
sprig settings show --json
sprig settings set --start 8000 --end 9000 --restrict 8080,8443 --json
```

## Common shapes (see references/config-cheatsheet.md for full JSON)

- **Single-app, no infra** — one module, one `.env`, `inputs` = a port + an API URL.
- **Consumer with infra** — one module with `env` + a `compose` overriding postgres port/container.
- **Monorepo** — two modules (`apps/web`, `apps/api`) sharing repo-level inputs; api owns the compose.
- **Auth0 front end** — an input with `allowedPorts: "8100-8103"`.
