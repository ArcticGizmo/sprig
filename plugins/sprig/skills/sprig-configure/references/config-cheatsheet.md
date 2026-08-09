# sprig config cheatsheet

Condensed from the sprig `docs/config-reference.md`. Two surfaces; data flows **stack → repo**.

## `.sprig.json` (repo config, schema 3) — the CONSUMER

Committed in the repo. The only file sprig adds to a source tree. Unknown top-level keys are rejected.

| Field | Type | Req | Meaning |
|---|---|---|---|
| `schema` | int | yes | `3` |
| `name` | string | yes | Logical repo name; the stack's binding key for this repo |
| `inputs` | array | no | Values this repo needs. Declared **once**, shared across every module. `${sprig.<name>}` |
| `modules` | array | no | Slices of the repo, each with its own `env`/`compose`/`setup` and optional `path` |

### `inputs[]`
| Field | Req | Meaning |
|---|---|---|
| `name` | yes | Referenced as `${sprig.<name>}` |
| `example` | no | Shape the stack should supply (`5000`, `http://localhost:5000`) |
| `description` | no | Human hint |
| `allowedPorts` | no | Pin the feeding port to a set: `"8100-8103"`, `"8100,8101,8200"`, `"8100-8103,8200"` |

Every declared input **must** be bound by any stack that uses the repo (else hard failure at create).
`allowedPorts` must trace to exactly one `${sprig.ports.<name>}`; two inputs on the same port must agree.

### `modules[]`
| Field | Req | Meaning |
|---|---|---|
| `name` | yes | Unique within the repo; letters/digits/`-`/`_` |
| `path` | no | Subdir the module lives in (`apps/web`); env/compose resolve under it, setup runs in it. Omit for repo root |
| `env` | no | `.env.*` files to clobber (relative to `path`) |
| `compose` | no | Docker compose override declarations (relative to `path`) |
| `setup` | no | `string[]` commands run in the module's dir after create |

### `env[]`
| Field | Meaning |
|---|---|
| `file` | The `.env.*` file to seed + clobber (relative to `path`) |
| `templates` | Optional file(s) to seed the worktree copy from (use when the real file is gitignored — seed from `.env.template`) |
| `set` | `KEY: template` pairs; templates may use `${sprig.<input>}` / `${sprig.workspace}` |

sprig seeds from the source repo then injects a marker-delimited block at top+bottom so its values win.
The source repo is never touched.

### `compose[]`
| Field | Meaning |
|---|---|
| `file` | Compose file (relative to `path`); effective path (`path`+`file`) unique across the repo |
| `overrides[].path` | YAML path segments, e.g. `["services","postgres","ports","0"]` |
| `overrides[].template` | Value to place there (a `${sprig...}` template) |

### `setup[]`
Ordered commands run in the module's dir (`<worktree>/<path>`) after worktree→env→compose. Via the
platform shell (`cmd /c` on Windows). First non-zero exit stops the rest. **No `${sprig.*}` expansion.**
On failure: a **soft warning**, the workspace is kept (unlike env/compose failures, which roll back).

### Templates
Only `${sprig.<input>}` (a declared input) and `${sprig.workspace}` (the workspace slug) are valid.

---

## Stack (schema 2) — the PRODUCER

Central store (`%LOCALAPPDATA%\sprig\stacks\<name>.json`), never in a repo.

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | `2` |
| `name` | string | `^[A-Za-z0-9._+-]+$` |
| `repos` | string[] | Repos by **registry name**; each must exist in the registry |
| `ports` | string[] | Named ports the stack owns; each → a real non-colliding number per workspace |
| `bindings` | object | `bindings[repo][input] = expression` |
| `shares` | array | Ports consumed by 2+ repos (auto-derived; don't hand-edit) |

- Reference a port from a binding as `${sprig.ports.<name>}`.
- A binding is a **literal** or a **template** over `${sprig.ports.<name>}` / `${sprig.workspace}`.
- **Sharing a value between repos = point both bindings at the same port.**
- Same-named inputs in different repos are independent.

---

## Worked examples

### Single-app, no infra
```json
{
  "schema": 3,
  "name": "sprig-example-vue",
  "inputs": [
    { "name": "frontend", "example": "3000", "description": "Vite dev host port" },
    { "name": "apiUrl",   "example": "http://localhost:4000", "description": "backend base URL" }
  ],
  "modules": [
    { "name": "app", "env": [
      { "file": ".env", "set": { "PORT": "${sprig.frontend}", "VITE_API_URL": "${sprig.apiUrl}" } }
    ] }
  ]
}
```

### Consumer with infra
```json
{
  "schema": 3,
  "name": "dotnet-api",
  "inputs": [
    { "name": "port",   "example": "5000", "description": "ASP.NET host port" },
    { "name": "dbPort", "example": "5432", "description": "postgres host port" }
  ],
  "modules": [
    { "name": "app",
      "env": [ { "file": ".env", "set": {
        "PORT": "${sprig.port}",
        "ConnectionStrings__Default": "Host=localhost;Port=${sprig.dbPort};Database=librarydb;Username=library;Password=library_pass"
      } } ],
      "compose": [ { "file": "docker-compose.yml", "overrides": [
        { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
        { "path": ["services","postgres","ports","0"],       "template": "${sprig.dbPort}:5432" }
      ] } ]
    }
  ]
}
```

### Monorepo (web + api in one repo)
```json
{
  "schema": 3,
  "name": "sprig-example-mono",
  "inputs": [
    { "name": "webPort", "example": "3000" },
    { "name": "apiPort", "example": "5000" },
    { "name": "dbPort",  "example": "5432" }
  ],
  "modules": [
    { "name": "web", "path": "apps/web",
      "env": [ { "file": ".env.local", "templates": [".env"], "set": {
        "VITE_PORT": "${sprig.webPort}", "VITE_API_URL": "http://localhost:${sprig.apiPort}" } } ],
      "setup": [ "npm ci" ] },
    { "name": "api", "path": "apps/api",
      "env": [ { "file": ".env", "set": { "PORT": "${sprig.apiPort}" } } ],
      "compose": [ { "file": "docker-compose.yml", "overrides": [
        { "path": ["services","postgres","ports","0"], "template": "${sprig.dbPort}:5432" } ] } ],
      "setup": [ "dotnet restore" ] }
  ]
}
```

### Auth0 front end with pinned callback ports
```json
{
  "schema": 3,
  "name": "auth0-spa",
  "inputs": [
    { "name": "frontend", "example": "3000", "allowedPorts": "8100-8103",
      "description": "Vite dev host port — must be a registered Auth0 callback port" }
  ],
  "modules": [
    { "name": "app", "env": [ { "file": ".env", "set": { "PORT": "${sprig.frontend}" } } ] }
  ]
}
```

### Stack: two repos sharing a port (web+api)
`api_port` is consumed by both — the frontend's `apiUrl` points at the API's `port`.
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
(`shares` is derived automatically for `api_port`. A schema-1 stack is migrated to schema 2 on load.)
