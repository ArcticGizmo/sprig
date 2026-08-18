# Configuration reference

sprig has two configuration surfaces:

- **Repo config** — `.sprig.json`, committed inside each repo. A repo is **self-describing**: it
  declares what it *provides* to others and what it *needs* from them.
- **Map** — stored centrally (`%LOCALAPPDATA%\sprig\maps\<name>.json`), never in a repo. Names the
  repos you compose and records only the *deviations* from automatic wiring.

Wiring is **derived by capability name**: a repo's `need` is satisfied by whichever selected repo
`provides` that capability. There is no one-directional "the map supplies every value" step — the
values a workspace runs on come from the repos' own declarations, resolved at checkout.

---

## Repo config — `.sprig.json`

The only file sprig ever adds to a source repo's tracked tree. It declares only that repo's own
surface. Unknown top-level keys are rejected by the validator.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `schema` | int | yes | Config schema version. Currently `1`. |
| `name` | string | yes | Logical repo name. The repo's identity in the registry and in a map. |
| `modules` | array | no | The repo's **modules** — each a slice of the repo with its own `provides`/`needs`/`env`/`compose`/`setup` and an optional `path`. A single-app repo has one module; a monorepo has several. |

> **Single-app sugar.** A one-slice repo may put `provides`, `needs`, `env`, `compose` and `setup`
> at the **top level** instead of inside a `modules[]` entry; sprig folds them into one implicit
> module named `app` (its `path` is the repo root). A monorepo lists `modules` explicitly. Both
> shapes behave identically — every consumer iterates the *effective* modules.

### `provides[]` — what the module offers

Each entry is a **capability** this module owns and hands to others. Capabilities are matched
between repos by **name** (the contract), so a provider and a consumer only have to agree on the
name.

A capability is **one name that comes in many shapes**. It owns real **ports** (the one thing a repo
genuinely owns — auto-allocated per workspace) and exposes **shapes**: derived strings built over
those ports (a url, a connection string). Both are addressed uniformly as
`${sprig.<capability>.<output>}`, where `<output>` is a port or a shape name — so **name the service,
not the socket**: `vite-server.port` reads as a hierarchy, while `vite-port.port` stutters.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `capability` | string | yes | The contract name — the head of every `${sprig.<capability>.<output>}` reference and the wiring key other repos' `needs` match against. Name the *service* (`vite-server`), not one of its shapes. |
| `ports` | object | one of `ports`/`shapes` | `port-name → spec`. Real, non-colliding host ports allocated per workspace. Each spec is `true` (any host port) or `{ "allowed": "8100-8103" }` (pinned — see below). The app always exports a single port named `port`. |
| `shapes` | object | one of `ports`/`shapes` | `shape-name → template`. Derived strings built from this capability's **own** ports (plus `${sprig.workspace}`), e.g. `"url": "http://localhost:${sprig.<thisCapability>.port}"`. |

A capability must declare at least one port **or** shape; port and shape names share one namespace
(a name is one or the other, never both). Any output is referenced anywhere in the repo as
`${sprig.<capability>.<output>}`.

```jsonc
"provides": [
  { "capability": "vite-server",
    "ports":  { "port": true },
    "shapes": { "url": "http://localhost:${sprig.vite-server.port}" } }
]
```

#### `allowed` — pin a port to a fixed set

By default a provided port is drawn from the whole range configured in **Settings**. Some ports are
special — the classic case is an Auth0 front end whose callback URLs
(`http://localhost:<port>/callback`) must be pre-registered per port. Give the port an object with an
`allowed` set instead of bare `true`, and sprig only ever allocates from that set:

```jsonc
"ports": { "port": { "allowed": "8100-8103" } }
```

- **Spec syntax:** a comma-separated list of single ports and inclusive ranges — `"8100-8103"`,
  `"8100,8101,8200"`, or `"8100-8103,8200"`. Whitespace is ignored.
- **Precedence:** the allowed set is used as-is (a port outside the Settings range is still
  allocatable), but a port marked **restricted** in Settings is still skipped.
- **Capacity:** the set size caps how many workspaces can hold that port at once. When every allowed
  port is taken, checkout fails with a clear "no free port left in the allowed set …" message.

### `needs[]` — what the module consumes

Each entry is a capability this module requires from **another** repo (or a sibling module). Needs
are always explicit.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `capability` | string | yes | The contract name this module requires. Matched against others' `provides`. |
| `as` | string | no | A local **alias** — reference the need's outputs as `${sprig.<as>.<output>}` instead of `${sprig.<capability>.<output>}`. Useful when a name is awkward, or a module needs two things of the same shape. |

Reference a need's output as `${sprig.<capability-or-alias>.<output>}`. The output lives in **another
repo**, so any output name is accepted while authoring and validated at resolve time (against the
provider actually chosen for that capability). A need never references a raw port — a repo owns its
own ports and consumes finished values.

```jsonc
"needs": [ { "capability": "api", "as": "backend" } ],
"env":   [ { "file": ".env", "set": { "VITE_API_URL": "${sprig.backend.url}" } } ]
```

### `modules[]` — slices of the repo

Each module is a slice — a monorepo package/app, or simply "the whole repo" for a single-app
project. It carries its own `provides`/`needs`/`env`/`compose`/`setup` and an optional `path`.

| Field | Type | Required | Meaning |
|---|---|---|---|
| `name` | string | yes | The module's name (the tab label in the app). Unique within the repo; letters/digits/`-`/`_`. |
| `path` | string | no | The subdirectory the module lives in (e.g. `apps/web`). Its `env`/`compose` file paths resolve **under** it, and its `setup` runs **in** `<worktree>/<path>`. Omit (or `""`) for the repo root. |
| `provides` | array | no | Capabilities this module offers (see above). |
| `needs` | array | no | Capabilities this module consumes (see above). |
| `env` | array | no | Which `.env.*` files to clobber and which keys to set (relative to `path`). See below. |
| `compose` | array | no | Docker compose override declarations (relative to `path`), one per compose file. See below. |
| `setup` | string[] | no | Free-form commands run in the module's directory after the worktree is created (e.g. `npm ci`). See below. |

`provides`/`needs` are **per-module** — a sibling module in the same repo can provide exactly what
another module needs, and it wires locally (see [wiring](#wiring--how-needs-find-providers)). The
`env`, `compose` and `setup` entries have the same shape whether a repo has one module or several — a
module just scopes them to its `path`. Two modules may each override a `docker-compose.yml` as long
as they sit at different paths (the effective path — `path` + file — must be unique across the repo).

### `env[]` — `.env.*` clobbering

Each entry targets one `.env.*` file and sets keys in it. Values are `${sprig...}` templates.

| Field | Type | Meaning |
|---|---|---|
| `file` | string | The `.env.*` file to seed + clobber (relative to the module's `path`). |
| `templates` | string[] | Optional. File(s) to seed the worktree's copy from before the override block. |
| `set` | object | `KEY: template` pairs. Each template may reference `${sprig.<cap>.<out>}` / `${sprig.workspace}`. |

sprig seeds the worktree's copy from the source repo, then injects a marker-delimited block at the
**top and bottom** so its values win regardless of the framework's load order. The source repo is
never touched.

**`templates` — seed from a different file.** By default the seed is the target `file`'s own content
in the source repo (empty if it doesn't exist). That's a problem when the real file is gitignored and
never committed — the repo instead commits a `.env.template`/`.env.example`. List those here and the
worktree's copy is seeded by **merging** the target file with the templates, in precedence order:

- **target `file` first**, then each template in the order listed (missing ones skipped).
- The seed is **de-duplicated by key** — a key is taken from the first (highest-precedence) source
  that defines it, so a template only fills in keys the target file (or an earlier template) didn't
  already provide. A template never overrides a value a higher-precedence source already gave.

The merged seed is injected between sprig's marker block, so anything you set in `set` still wins over
every seeded value regardless. Editable per env file in the app's repo editor ("Seed from templates").

```jsonc
{ "file": ".env.local", "templates": [".env.template"], "set": { "PORT": "${sprig.web.port}" } }
```

### `compose[]` — docker infra overrides (optional)

Omit (or leave empty) if the module has no infrastructure. `compose` is an **array** — a module may
override several compose files. For each entry sprig parses that compose file, applies its path-based
overrides, and writes a separate generated compose file into the central store for the workspace; all
of a workspace's generated files (across every module) are brought up together under one docker-compose
project. `sprig init` discovers compose files recursively (skipping build/vendor directories like
`node_modules`, `dist`, `obj`) and proposes one entry each — remove any you don't want overridden.

| Field | Type | Meaning |
|---|---|---|
| `file` | string | Path to a compose file (relative to the module's `path`). Its effective path (`path` + `file`) must be unique across the repo. |
| `overrides[]` | array | Path-based value replacements. |
| `overrides[].path` | string[] | YAML path segments, e.g. `["services","postgres","ports","0"]`. |
| `overrides[].template` | string | Resolved value to place at that path (a `${sprig...}` template). |

### `setup[]` — post-create commands (optional)

After sprig materialises a workspace (worktree → `.env` → compose), the worktree is a fresh checkout
with no installed dependencies. `setup` is an ordered list of free-form commands that run at that
point so the worktree is actually runnable — the declarative equivalent of `cd`-ing in and running
the install by hand.

```jsonc
{ "schema": 1, "name": "vue-app", "modules": [ { "name": "app", "setup": ["npm ci"] } ] }
```

- **Where:** each command runs in the module's directory (`<worktree>/<path>`, or the worktree root
  when the module has no `path`) — the isolated copy, not the source repo.
- **How:** via the platform shell — `cmd.exe /c <command>` on Windows, `/bin/sh -c <command>`
  elsewhere — so ordinary shell commands work. Keep each entry a single command; complex quoting or
  `&&` chaining inside one entry can be finicky on Windows `cmd`.
- **Order & short-circuit:** commands run top-to-bottom; the first one that exits non-zero **stops the
  rest** (a later step usually depends on an earlier one).
- **On failure — a soft warning, not a rollback.** Unlike a bad worktree/env/compose (which rolls the
  whole create back), a failing setup command leaves the workspace **created**. The failure is
  recorded on the instance and surfaced as a warning in the app and CLI, so you can finish the install
  by hand in the worktree.
- **No substitution:** setup commands are literal — `${sprig.*}` is not expanded inside them.

Edit the list in the app's repo editor ("Setup commands").

### Templates

Anywhere a template is allowed you may reference:

- `${sprig.<capability>.<output>}` — an output of a capability this module **provides** (checked by
  exact output name) **or** one it **needs** (any output — it resolves in the providing repo).
- `${sprig.workspace}` — the workspace slug (useful for `container_name` suffixes).

Any reference that isn't a provided output, a needed capability, or `workspace` is rejected by the
validator.

### Example — provider with infra (`postgres-api`)

One module (the whole repo) that **provides** an `api` capability (its listen port + a derived URL)
and a `db` capability (its database's host port), and owns the compose file that serves them.

```json
{
  "schema": 1,
  "name": "postgres-api",
  "modules": [
    { "name": "app",
      "provides": [
        { "capability": "api",
          "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.api.port}" } },
        { "capability": "db",
          "ports": { "port": true } }
      ],
      "env": [
        { "file": ".env", "set": {
            "PORT": "${sprig.api.port}",
            "ConnectionStrings__Default": "Host=localhost;Port=${sprig.db.port};Database=librarydb;Username=library;Password=library_pass"
        } }
      ],
      "compose": [
        { "file": "docker-compose.yml", "overrides": [
            { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
            { "path": ["services","postgres","ports","0"],       "template": "${sprig.db.port}:5432" }
        ] }
      ]
    }
  ]
}
```

### Example — consumer, no infra (`vue-web`)

Provides its own dev-server `web` port; **needs** the `api` capability above, and references the
finished `url` its provider derives — so it never has to know the API's port.

```json
{
  "schema": 1,
  "name": "vue-web",
  "modules": [
    { "name": "app",
      "provides": [ { "capability": "web", "ports": { "port": true } } ],
      "needs":    [ { "capability": "api" } ],
      "env": [
        { "file": ".env", "set": {
            "PORT": "${sprig.web.port}",
            "VITE_API_URL": "${sprig.api.url}"
        } }
      ]
    }
  ]
}
```

### Example — monorepo (`web` + `api` in one repo)

Two modules in one repo. The `api` module **provides** `mono-api`; the `web` module **needs** it — and
because the provider is a sibling in the *same* repo, it wires **locally** with no map involved
(nearest-wins). Each module's env resolves under its `path`; the `api` module owns the compose file.

```json
{
  "schema": 1,
  "name": "acme-mono",
  "modules": [
    { "name": "api", "path": "apps/api",
      "provides": [
        { "capability": "mono-api",
          "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.mono-api.port}" } },
        { "capability": "db", "ports": { "port": true } }
      ],
      "env": [ { "file": ".env", "set": { "PORT": "${sprig.mono-api.port}" } } ],
      "compose": [ { "file": "docker-compose.yml", "overrides": [
          { "path": ["services","postgres","ports","0"], "template": "${sprig.db.port}:5432" }
      ] } ],
      "setup": [ "dotnet restore" ] },

    { "name": "web", "path": "apps/web",
      "provides": [ { "capability": "web", "ports": { "port": true } } ],
      "needs":    [ { "capability": "mono-api" } ],
      "env": [ { "file": ".env.local", "templates": [".env"], "set": {
          "VITE_PORT": "${sprig.web.port}",
          "VITE_API_URL": "${sprig.mono-api.url}"
      } } ],
      "setup": [ "npm ci" ] }
  ]
}
```

### Example — Auth0 front end with pinned callback ports

The `web` port must be one of four pre-registered Auth0 callback ports, so at most four workspaces can
run at once — sprig only ever allocates `8100`–`8103` for it. The restriction rides on the provided
port.

```json
{
  "schema": 1,
  "name": "auth0-spa",
  "modules": [
    { "name": "app",
      "provides": [ { "capability": "web", "ports": { "port": { "allowed": "8100-8103" } } } ],
      "env": [ { "file": ".env", "set": { "PORT": "${sprig.web.port}" } } ]
    }
  ]
}
```

---

## Wiring — how needs find providers

When a workspace is checked out, sprig matches each module's `needs` to a provider **by capability
name**, in this order (nearest-wins):

1. **A sibling in the same repo.** A module whose need is provided by another module of the *same*
   repo always wins — local wiring, resolved with no map involvement.
2. **A single map-wide provider.** Otherwise, if exactly one selected repo provides the capability,
   that one is used.
3. **Ambiguous — more than one provider.** If two or more selected repos provide it, the map must
   pick one (a `wiring` entry). Until it does, that need is flagged **ambiguous**.
4. **A gap — no provider.** If nothing in the selection provides it, a map `default` supplies a
   literal fallback (e.g. a shared staging URL); with no default, it's an **unmet need** — a hard
   failure at checkout, with the gap list naming `repo.module needs '<capability>'`.

Because wiring is derived from the repos' own declarations, a monorepo is its own **local map**:
sibling modules wire to each other, and only the needs nothing local satisfies bubble up to the map.

---

## Map — `maps/<name>.json`

A map is an **open graph** of repos you take slices of. It lists the repos in play and stores only
the **deviations** from automatic wiring — which provider wins when several could, and a manual
fallback for a need whose provider you've left out. Everything else is derived from the repos'
provides/needs at checkout, so editing a map never invalidates an existing workspace. Multiple maps
are first-class (different working styles, or unrelated projects). Lives in the central store, never
inside a repo. Authored in the app's **Maps** page, or imported with `sprig map import`.

| Field | Type | Meaning |
|---|---|---|
| `schema` | int | Schema version. Currently `1`. |
| `name` | string | Map name. Must match `^[A-Za-z0-9._+-]+$`. |
| `repos` | array | The repos in the map. Each entry is a bare **registry name** (`"acme"`), or an object `{ "name": …, "repo": <git-url> }`. |
| `wiring` | object | `wiring[repo][capability] = providerCapability` — disambiguation, only needed when more than one selected repo provides a capability a repo needs. |
| `defaults` | object | `defaults[repo][capability][output] = literal` — a manual fallback for a need whose provider isn't in the current selection, so a partial checkout resolves instead of reporting a gap. |
| `maxSlots` | int | Optional pool ceiling — the max warm workspaces of this map. Defaults to **5** when unset. |

### `repos[]` — names, and portable URLs

A bare string is a local registry name. An object carries a `name` plus an optional git `repo` URL:

```jsonc
"repos": [ "acme-api", { "name": "billing", "repo": "git@github.com:acme/billing.git" } ]
```

The URL lets a **shared** map bootstrap a repo on a machine that hasn't registered it: at checkout,
an unregistered repo with a URL is cloned into the store and registered. The URL is treated as the
**upstream/canonical** source (the clone's `origin`), so a fork workflow re-points origin afterward.

### Example — two repos, auto-wired by name

`vue-web` needs `api`; `postgres-api` provides it, and it's the only provider — so the map needs no
`wiring` and no `defaults`. It's just the two repos.

```json
{
  "schema": 1,
  "name": "web+api",
  "repos": ["vue-web", "postgres-api"],
  "maxSlots": 4
}
```

At checkout `web`, `api.port`, `db.port` are allocated; `vue-web`'s `VITE_API_URL` resolves from
`postgres-api`'s derived `api.url`, pointing at *its* isolated API.

### Example — a `defaults` fallback for a partial checkout

Check `vue-web` out on its own (`--only vue-web`, or a map that omits the backend) and its `api` need
has no provider in the selection. Rather than fail, the map supplies a finished value:

```json
{
  "schema": 1,
  "name": "web-standalone",
  "repos": ["vue-web"],
  "defaults": {
    "vue-web": { "api": { "url": "https://staging.acme.dev" } }
  }
}
```

`vue-web`'s `${sprig.api.url}` now resolves to the staging URL. (When more than one repo could
provide `api`, you'd instead add a `wiring` entry naming the winner.)

---

## Resolution at checkout

When you check out (or `create`) a workspace from a map slice, sprig:

1. Allocates a real, non-colliding number for each provided `port` across the selected repos.
2. Resolves each module's `needs` to a provider — nearest-wins sibling, then a single map-wide
   provider, then the map's `wiring`/`defaults`. An unmet need is a **hard failure** with rollback and
   the gap list.
3. For each module, clobbers its `.env.*` files and generates its compose file(s) from the resolved
   capability scope, resolving paths under the module's `path`.
4. Runs each module's `setup` commands in the module's directory (a failure here is a **soft
   warning** — the workspace is kept, not rolled back).

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
