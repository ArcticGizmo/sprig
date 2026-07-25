# Configuration reference

sprig has two configuration surfaces:

- **Repo config** — `.sprig.json`, committed inside each repo. Declares what that repo *consumes*.
- **Stack definition** — stored centrally (`%LOCALAPPDATA%\sprig\stacks\<name>.json`), never in a
  repo. Declares the repos it composes and *produces* every value they need.

The data flow is one-directional: **stack → repo**. A repo declares inputs; the stack binds them.

---

## Repo config — `.sprig.json`

The only file sprig ever adds to a source repo's tracked tree. It declares only that repo's own
isolation surface. Unknown top-level keys are rejected by the validator.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `schema` | int | yes | Config schema version. Currently `2`. |
| `name` | string | yes | Logical repo name. Used as the stack's binding key for this repo. |
| `inputs` | array | no | The values this repo needs, referenced as `${sprig.<name>}` in its templates. |
| `env` | array | no | Which `.env.*` files to clobber and which keys to set. |
| `compose` | array | no | Docker compose override declarations (path-based), one entry per compose file. Omit or leave empty if the repo has no infra. |

### `inputs[]` — what the repo consumes

A repo is a pure consumer. Each input is a value the **stack** must supply.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `name` | string | yes | Referenced elsewhere in this file as `${sprig.<name>}`. |
| `example` | string | no | The shape the stack should supply (e.g. `5000`, `http://localhost:5000`). |
| `description` | string | no | Human hint shown while authoring the binding. |
| `allowedPorts` | string | no | Restrict the port feeding this input to a fixed set (see below). |

Every declared input **must** be bound by any stack that uses the repo. An unbound input is a hard
failure at create time (the error names the repo, the input, and its example).

#### `allowedPorts` — pin an input to a fixed set of ports

By default the stack port feeding an input is drawn from the whole range configured in **Settings**.
Some values are only valid for a *specific* set of ports — the classic case is an Auth0 front end
whose callback URLs (`http://localhost:<port>/callback`) must be pre-registered per port. Set
`allowedPorts` on that input and sprig will only ever allocate from the set:

- **Spec syntax:** a comma-separated list of single ports and inclusive ranges — `"8100-8103"`,
  `"8100,8101,8200"`, or `"8100-8103,8200"`. Whitespace is ignored.
- **How it's applied:** sprig traces the input through the stack's binding to the single
  `${sprig.ports.<name>}` port that feeds it, and constrains *that* port's allocation. The binding
  must reference exactly one stack port — a literal (no port) or a multi-port expression is a hard
  error at create time, so a restriction is never silently ignored.
- **Precedence:** the allowed set is used as-is (a port outside the Settings range is still
  allocatable), but a port marked **restricted** in Settings is still skipped.
- **Capacity:** the set size caps how many instances can run at once. When every allowed port is
  taken, `create` fails with a clear "no free port left in the allowed set …" message.
- Two inputs that resolve to the *same* stack port must agree — sprig intersects their sets and
  errors if nothing is common.

### `env[]` — `.env.*` clobbering

Each entry targets one `.env.*` file and sets keys in it. Values are `${sprig...}` templates.

| Field | Type | Meaning |
|---|---|---|
| `file` | string | The `.env.*` file to seed + clobber (relative to the repo root). |
| `templates` | string[] | Optional. File(s) to seed the worktree's copy from before the override block. |
| `set` | object | `KEY: template` pairs. Each template may reference `${sprig.<input>}` / `${sprig.workspace}`. |

sprig seeds the worktree's copy from the source repo, then injects a marker-delimited block at the
**top and bottom** so its values win regardless of the framework's load order. The source repo is
never touched.

**`templates` — seed from a different file.** By default the seed is the target `file`'s own content
in the source repo (empty if it doesn't exist). That's a problem when the real file is gitignored and
never committed — the repo instead commits a `.env.template`/`.env.example`. List those here and the
worktree's copy is seeded from them (concatenated in order; missing ones skipped) before sprig's block
is injected. Editable per env file in the app's repo editor ("Seed from templates").

```jsonc
{ "file": ".env.local", "templates": [".env.template"], "set": { "PORT": "${sprig.frontend}" } }
```

### `compose[]` — docker infra overrides (optional)

Omit (or leave empty) if the repo has no infrastructure. `compose` is an **array** — a repo may
override several compose files (monorepos often keep more than one). For each entry sprig parses that
compose file, applies its path-based overrides, and writes a separate generated compose file into the
central store for the workspace; all of a workspace's generated files are brought up together under
one docker-compose project. `sprig init` discovers compose files recursively (skipping build/vendor
directories like `node_modules`, `dist`, `obj`) and proposes one entry each — remove any you don't
want overridden.

| Field | Type | Meaning |
|---|---|---|
| `file` | string | Path to a compose file (relative to the repo root). Must be unique within the array. |
| `overrides[]` | array | Path-based value replacements. |
| `overrides[].path` | string[] | YAML path segments, e.g. `["services","postgres","ports","0"]`. |
| `overrides[].template` | string | Resolved value to place at that path (a `${sprig...}` template). |

### Templates

Anywhere a template is allowed you may reference:

- `${sprig.<input>}` — one of this repo's declared inputs (supplied by the stack).
- `${sprig.workspace}` — the workspace slug (useful for `container_name` suffixes).

Any reference that isn't a declared input or `workspace` is rejected by the validator.

### Example — consumer with infra (`dotnet-api`)

```json
{
  "schema": 2,
  "name": "dotnet-api",
  "inputs": [
    { "name": "port",   "example": "5000", "description": "ASP.NET host port" },
    { "name": "dbPort", "example": "5432", "description": "postgres host port" }
  ],
  "env": [
    { "file": ".env", "set": {
        "PORT": "${sprig.port}",
        "ConnectionStrings__Default": "Host=localhost;Port=${sprig.dbPort};Database=librarydb;Username=library;Password=library_pass"
    } }
  ],
  "compose": [
    { "file": "docker-compose.yml", "overrides": [
        { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
        { "path": ["services","postgres","ports","0"],       "template": "${sprig.dbPort}:5432" }
    ] }
  ]
}
```

### Example — consumer, no infra (`sprig-example-vue`)

```json
{
  "schema": 2,
  "name": "sprig-example-vue",
  "inputs": [
    { "name": "frontend", "example": "3000", "description": "Vite dev host port" },
    { "name": "apiUrl",   "example": "http://localhost:4000", "description": "backend base URL" }
  ],
  "env": [
    { "file": ".env", "set": {
        "PORT": "${sprig.frontend}",
        "VITE_API_URL": "${sprig.apiUrl}"
    } }
  ]
}
```

### Example — Auth0 front end with pinned callback ports

The `frontend` port must be one of four pre-registered Auth0 callback ports, so at most four
instances can run at once — sprig only ever allocates `8100`–`8103` for it. The stack binds
`frontend` to `${sprig.ports.frontend_port}` as usual; the restriction rides on the repo.

```json
{
  "schema": 2,
  "name": "auth0-spa",
  "inputs": [
    { "name": "frontend", "example": "3000", "allowedPorts": "8100-8103",
      "description": "Vite dev host port — must be a registered Auth0 callback port" }
  ],
  "env": [
    { "file": ".env", "set": { "PORT": "${sprig.frontend}" } }
  ]
}
```

---

## Stack definition

A stack composes 1+ registered repos, owns a set of named ports, and — per repo — binds each of
that repo's declared inputs. It lives in the central store, never inside a repo.

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | Schema version. Currently `2`. |
| `name` | string | Stack name. Must match `^[A-Za-z0-9._+-]+$`. |
| `repos` | string[] | Repos in the stack, by **registry name**. Each must exist in the repo registry. |
| `ports` | string[] | Named ports the stack owns. Each is allocated a real, non-colliding number per workspace. |
| `bindings` | object | `bindings[repo][input] = expression` — supplies each repo's declared inputs. |
| `shares` | array | Ports shared by 2+ repos, made explicit (see below). Optional; back-filled on load for older files. |

### Ports

Just names. At create time each becomes a real, non-colliding number for that workspace, drawn from
the range configured in **Settings** (default `8000–8999`), skipping any restricted ports. Reference
an allocated port from a binding as `${sprig.ports.<name>}`. Two workspaces of the same stack get
independent port sets, so they run side by side.

### Bindings

For each repo, supply every input it declares. An expression is either:

- a **literal** (e.g. `http://localhost:4000`), or
- a **template** over `${sprig.ports.<name>}` / `${sprig.workspace}`.

Same-named inputs in different repos are **independent** — bind each one individually. Sharing a
value between repos is done by pointing both bindings at the same stack port.

### Shares — sharing made explicit

`bindings` alone stays the single source of truth for resolution: at create time sprig only ever
reads the binding expressions. But "two repos deliberately point at the same port" is a real
relationship the builder, port-rename, and wiring canvas need to know about — so schema 2 records it
explicitly.

| Field | Type | Meaning |
|---|---|---|
| `shares[].port` | string | A declared stack port that more than one repo consumes. |
| `shares[].consumers[]` | array | The `{ repo, input }` pairs wired to that port. |

Each `shares` entry is validated on save: the port must be declared, each consumer must be a repo in
the stack, and that consumer's binding **must** reference `${sprig.ports.<port>}`. sprig keeps
`shares` in step with `bindings`; you don't edit it by hand.

**Migration.** A schema-1 stack has no `shares`. On load sprig derives it from the bindings — any
port referenced by two or more `(repo, input)` bindings becomes a share — bumps the file to schema 2
in memory, and persists it the next time the stack is saved (or immediately on import). Nothing you
need to do.

### Example — two repos sharing a port (`web+api`)

The API listens on `api_port`; the frontend's `apiUrl` points at that same port — so the web app
talks to *its* isolated API, not another workspace's.

```json
{
  "schema": 1,
  "name": "web+api",
  "repos": ["sprig-example-vue", "dotnet-api"],
  "ports": ["frontend_port", "api_port", "postgres_port"],
  "bindings": {
    "sprig-example-vue": {
      "frontend": "${sprig.ports.frontend_port}",
      "apiUrl":   "http://localhost:${sprig.ports.api_port}"
    },
    "dotnet-api": {
      "port":   "${sprig.ports.api_port}",
      "dbPort": "${sprig.ports.postgres_port}"
    }
  },
  "shares": [
    { "port": "api_port", "consumers": [
        { "repo": "sprig-example-vue", "input": "apiUrl" },
        { "repo": "dotnet-api",        "input": "port" }
    ] }
  ]
}
```

`api_port` is consumed by both repos, so it appears in `shares`. `frontend_port` and `postgres_port`
each have a single consumer, so they don't.

### Example — one repo, literal binding (`web-only`)

No backend in the stack, so `apiUrl` is a plain literal.

```json
{
  "schema": 1,
  "name": "web-only",
  "repos": ["sprig-example-vue"],
  "ports": ["frontend_port"],
  "bindings": {
    "sprig-example-vue": {
      "frontend": "${sprig.ports.frontend_port}",
      "apiUrl":   "http://localhost:4000"
    }
  }
}
```

---

## Resolution at create time

When you `create` a workspace from a stack, sprig:

1. Allocates a real, non-colliding number for each stack port.
2. For each repo, evaluates every input's binding to build that repo's input scope.
3. Clobbers the repo's `.env.*` files and generates its compose file from that scope.

Any declared input without a binding is a **hard failure** (with rollback) — the error names the
repo, the input, and its example, so you know exactly what to add.

---

## Machine settings — `settings.json`

Machine-local, user-configurable settings, stored centrally
(`%LOCALAPPDATA%\sprig\settings.json`) and edited from the app's **Settings** page — never inside a
repo. Both the app and the CLI read them, so allocation behaves the same either way.

| Field | Type | Default | Meaning |
|---|---|---|---|
| `portRangeStart` | int | `8000` | First port sprig may allocate to a workspace (inclusive). |
| `portRangeEndExclusive` | int | `9000` | One past the last allocatable port (exclusive) — so the default range is `8000–8999`. |
| `restrictedPorts` | int[] | `[]` | Ports never allocated, even inside the range (deduped + sorted on save). |

```json
{
  "portRangeStart": 8000,
  "portRangeEndExclusive": 9000,
  "restrictedPorts": [8080, 8443]
}
```

Changes apply to **new** allocations; workspaces that already hold ports keep them.
