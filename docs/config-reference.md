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
| `schema` | int | yes | Config schema version. Currently `1`. |
| `name` | string | yes | Logical repo name. Used as the stack's binding key for this repo. |
| `inputs` | array | no | The values this repo needs, referenced as `${sprig.<name>}` in its templates. |
| `env` | array | no | Which `.env.*` files to clobber and which keys to set. |
| `compose` | object | no | Docker compose override declaration (path-based). Omit if the repo has no infra. |

### `inputs[]` — what the repo consumes

A repo is a pure consumer. Each input is a value the **stack** must supply.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `name` | string | yes | Referenced elsewhere in this file as `${sprig.<name>}`. |
| `example` | string | no | The shape the stack should supply (e.g. `5000`, `http://localhost:5000`). |
| `description` | string | no | Human hint shown while authoring the binding. |

Every declared input **must** be bound by any stack that uses the repo. An unbound input is a hard
failure at create time (the error names the repo, the input, and its example).

### `env[]` — `.env.*` clobbering

Each entry targets one `.env.*` file and sets keys in it. Values are `${sprig...}` templates.

| Field | Type | Meaning |
|---|---|---|
| `file` | string | The `.env.*` file to seed + clobber (relative to the repo root). |
| `set` | object | `KEY: template` pairs. Each template may reference `${sprig.<input>}` / `${sprig.workspace}`. |

sprig seeds the worktree's copy from the source repo, then injects a marker-delimited block at the
**top and bottom** so its values win regardless of the framework's load order. The source repo is
never touched.

### `compose` — docker infra overrides (optional)

Omit this entirely if the repo has no infrastructure. When present, sprig parses the repo's compose
file, applies the path-based overrides, and writes a full generated compose file into the central
store for that workspace.

| Field | Type | Meaning |
|---|---|---|
| `file` | string | Path to the repo's compose file (relative to the repo root). |
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
  "schema": 1,
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
  "compose": { "file": "docker-compose.yml", "overrides": [
      { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
      { "path": ["services","postgres","ports","0"],       "template": "${sprig.dbPort}:5432" }
  ] }
}
```

### Example — consumer, no infra (`sprig-example-vue`)

```json
{
  "schema": 1,
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

---

## Stack definition

A stack composes 1+ registered repos, owns a set of named ports, and — per repo — binds each of
that repo's declared inputs. It lives in the central store, never inside a repo.

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | Schema version. Currently `1`. |
| `name` | string | Stack name. Must match `^[A-Za-z0-9._+-]+$`. |
| `repos` | string[] | Repos in the stack, by **registry name**. Each must exist in the repo registry. |
| `ports` | string[] | Named ports the stack owns. Each is allocated a real, non-colliding number per workspace. |
| `bindings` | object | `bindings[repo][input] = expression` — supplies each repo's declared inputs. |

### Ports

Just names. At create time each becomes a real, non-colliding number for that workspace. Reference
an allocated port from a binding as `${sprig.ports.<name>}`. Two workspaces of the same stack get
independent port sets, so they run side by side.

### Bindings

For each repo, supply every input it declares. An expression is either:

- a **literal** (e.g. `http://localhost:4000`), or
- a **template** over `${sprig.ports.<name>}` / `${sprig.workspace}`.

Same-named inputs in different repos are **independent** — bind each one individually. Sharing a
value between repos is done by pointing both bindings at the same stack port.

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
  }
}
```

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
