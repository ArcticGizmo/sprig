# sprig config cheatsheet

Condensed from sprig's `docs/config-reference.md`. Two surfaces: the repo's `.sprig.json` (self-describing) and a central map (composition plus deviations).

## `.sprig.json` (repo config, schema 1)

The only file sprig adds to a source tree. Unknown top-level keys are rejected.

| Field | Type | Req | Meaning |
|---|---|---|---|
| `schema` | int | yes | `1` |
| `name` | string | yes | Logical repo name; its identity in the registry and in maps |
| `modules` | array | no | Slices of the repo, each with its own `provides`/`needs`/`env`/`compose`/`setup` and optional `path` |

**Single-app sugar:** a one-slice repo may put `provides`, `needs`, `env`, `compose` and `setup` at the top level; sprig folds them into one module named `app` at the repo root.

### `modules[]`
| Field | Req | Meaning |
|---|---|---|
| `name` | yes | Unique within the repo; letters/digits/`-`/`_` |
| `path` | no | Subdir the module lives in (`apps/web`); env/compose resolve under it, setup runs in it. Omit for repo root |
| `provides` | no | Capabilities this module offers |
| `needs` | no | Capabilities this module consumes |
| `env` | no | `.env.*` files to clobber (relative to `path`) |
| `compose` | no | Docker compose override declarations (relative to `path`) |
| `setup` | no | `string[]` commands run in the module's dir after checkout |

### `provides[]`
| Field | Req | Meaning |
|---|---|---|
| `capability` | yes | Contract name other repos' `needs` match. Name the service (`api`), not a socket |
| `ports` | one of ports/shapes | `name → true` or `name → { "allowed": "8100-8103" }`. Real host ports allocated per workspace |
| `shapes` | one of ports/shapes | `name → template` built only from this capability's own outputs or `${sprig.workspace}` |
| `open` | no | The output that's a browsable entry point (usually `url`); listed by `ws urls`, opened by `ws browse` |
| `probe` | no | URL template GET'd by `ws urls --probe`; < 500 counts as up |

Port and shape names share one namespace. Every output is `${sprig.<capability>.<output>}`.

### `needs[]`
| Field | Req | Meaning |
|---|---|---|
| `capability` | yes | The capability required from another repo or a sibling module |
| `as` | no | Local alias: reference as `${sprig.<as>.<output>}` |

A need's outputs live in the provider, so any output name is accepted while authoring and checked at checkout.

### `env[]`
| Field | Meaning |
|---|---|
| `file` | The `.env.*` file to seed + clobber (relative to `path`) |
| `templates` | Optional file(s) to seed from when the real file is gitignored (`.env.template`). Target file wins, then templates in order; dedup by key |
| `set` | `KEY: template` pairs |

sprig seeds the worktree copy, then injects a marker-delimited block at top and bottom so its values win. The source repo is never touched.

### `compose[]`
| Field | Meaning |
|---|---|
| `file` | Compose file (relative to `path`); `path`+`file` unique across the repo |
| `overrides[].path` | YAML path segments, e.g. `["services","postgres","ports","0"]` |
| `overrides[].template` | Value to place there |

### `setup[]`
Ordered commands run in `<worktree>/<path>` via the platform shell (`cmd /c` on Windows). First non-zero exit stops the rest. **No `${sprig.*}` expansion.** A failure is a soft warning: the workspace is kept and flagged degraded.

### Template references
- `${sprig.<capability>.<output>}` — an output of a capability the module provides (exact name), or any output of a capability it needs.
- `${sprig.workspace}` — the workspace slug (for `container_name` suffixes).

### Wiring (nearest wins)
1. A sibling module in the same repo that provides it.
2. Exactly one selected repo that provides it.
3. More than one provider → **ambiguous** until the map's `wiring` picks one.
4. No provider → the map's `defaults`, else an **unmet need** (checkout fails and rolls back).

---

## Map (schema 1)

Central store (`%LOCALAPPDATA%\sprig\maps\<name>.json`), never in a repo. Import with `sprig map import <file>`.

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | `1` |
| `name` | string | `^[A-Za-z0-9._+-]+$` |
| `repos` | array | Registry names, or `{ "name": …, "repo": "<git-url>" }` to clone on a machine that lacks it |
| `wiring` | object | `wiring[repo][capability] = providerCapability` — only when >1 provider |
| `defaults` | object | `defaults[repo][capability][output] = literal` — a need with no provider selected |
| `maxSlots` | int | Pool ceiling; default 5 |

---

## Worked examples

### Provider with infra (`postgres-api`)
```json
{
  "schema": 1,
  "name": "postgres-api",
  "modules": [
    { "name": "app",
      "provides": [
        { "capability": "api",
          "ports": { "port": true },
          "shapes": { "url": "http://localhost:${sprig.api.port}" },
          "open": "url",
          "probe": "${sprig.api.url}/health" },
        { "capability": "db", "ports": { "port": true } }
      ],
      "env": [ { "file": ".env", "set": {
        "PORT": "${sprig.api.port}",
        "ConnectionStrings__Default": "Host=localhost;Port=${sprig.db.port};Database=librarydb;Username=library;Password=library_pass"
      } } ],
      "compose": [ { "file": "docker-compose.yml", "overrides": [
        { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
        { "path": ["services","postgres","ports","0"],       "template": "${sprig.db.port}:5432" }
      ] } ]
    }
  ]
}
```

### Consumer, no infra (`vue-web`)
```json
{
  "schema": 1,
  "name": "vue-web",
  "modules": [
    { "name": "app",
      "provides": [ { "capability": "web", "ports": { "port": true },
                      "shapes": { "url": "http://localhost:${sprig.web.port}" }, "open": "url" } ],
      "needs":    [ { "capability": "api" } ],
      "env": [ { "file": ".env", "set": { "PORT": "${sprig.web.port}", "VITE_API_URL": "${sprig.api.url}" } } ],
      "setup": [ "npm ci" ]
    }
  ]
}
```

### Monorepo (web + api in one repo, wired locally)
```json
{
  "schema": 1,
  "name": "acme-mono",
  "modules": [
    { "name": "api", "path": "apps/api",
      "provides": [
        { "capability": "mono-api", "ports": { "port": true },
          "shapes": { "url": "http://localhost:${sprig.mono-api.port}" } },
        { "capability": "db", "ports": { "port": true } }
      ],
      "env": [ { "file": ".env", "set": { "PORT": "${sprig.mono-api.port}" } } ],
      "compose": [ { "file": "docker-compose.yml", "overrides": [
        { "path": ["services","postgres","ports","0"], "template": "${sprig.db.port}:5432" } ] } ],
      "setup": [ "dotnet restore" ] },
    { "name": "web", "path": "apps/web",
      "provides": [ { "capability": "web", "ports": { "port": true } } ],
      "needs":    [ { "capability": "mono-api" } ],
      "env": [ { "file": ".env.local", "templates": [".env"], "set": {
        "VITE_PORT": "${sprig.web.port}", "VITE_API_URL": "${sprig.mono-api.url}" } } ],
      "setup": [ "npm ci" ] }
  ]
}
```

### Auth0 front end with pinned callback ports
```json
{
  "schema": 1,
  "name": "auth0-spa",
  "provides": [ { "capability": "web", "ports": { "port": { "allowed": "8100-8103" } } } ],
  "env": [ { "file": ".env", "set": { "PORT": "${sprig.web.port}" } } ]
}
```
At most four workspaces can hold the port at once; checkout fails clearly when the set is exhausted.

### Map: two repos, auto-wired
```json
{ "schema": 1, "name": "web+api", "repos": ["vue-web", "postgres-api"], "maxSlots": 4 }
```

### Map: front end alone, against staging
```json
{
  "schema": 1,
  "name": "web-standalone",
  "repos": ["vue-web"],
  "defaults": { "vue-web": { "api": { "url": "https://staging.acme.dev" } } }
}
```
