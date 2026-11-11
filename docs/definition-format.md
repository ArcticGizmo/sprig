# Definition format (schema 1)

A repo is described by a `sprig.yaml` at its root; an orchestration by `<id>.orchestration.yaml` anywhere, or `orchestration.yaml` when sprig keeps it. The file is the model: sprig reads it directly, nothing is compiled or run to read it.

```yaml
# yaml-language-server: $schema=https://arcticgizmo.github.io/sprig/schema/v1/repo.json
schema: 1
repo: shop-services
```

## Rules that hold everywhere

- **`schema` is required** and says which version of this format the file is written in. Sprig reads every version it has ever shipped, upgrading older ones as it reads them; it never rewrites the file unless asked.
- **Unknown keys are errors**, so a typo is caught rather than ignored.
- **Values are read exactly as written.** `on`, `1.10` and `0123` stay that text; nothing is turned into a number or a boolean. Only a value starting with `{` (or another character YAML gives a meaning) needs quotes.
- **Paths are relative to the repo's root** (an orchestration's: to the orchestration file), with forward slashes. An empty or missing `path` means the root.
- **Commands are literal.** `command`, `health` and every other command is run exactly as written, as you'd type it. `${…}` isn't allowed in them: running the app yourself wouldn't fill it in. Values reach an app only through the files it reads.

## References

`${…}` in a value is filled in when a workspace is planned. Each kind of reference reads differently, so where a value comes from is visible where it's used:

| Reference | What it is |
|---|---|
| `${catalog-api.port}` | The host port sprig gave the resource's first port in this workspace. |
| `${catalog-api.url}` | `scheme://localhost:port` for its first port. Only a port with a scheme has a url. |
| `${rabbitmq.ui.port}`, `${rabbitmq.ui.url}` | The same for a port by name. |
| `${mail.smtp-url}` | A value the resource declares (`values:`), or one its preset gives. |
| `${inputs.home-url}` | One of the repo's inputs. |
| `${workspace.name}` | The workspace's name, for things that must differ between workspaces. |
| `${shop-services.outputs.catalog-url}` | In an orchestration: a repo's output. |

`$${` writes a literal `${`.

## Ports

A port is reserved per workspace. It doesn't make anything listen: the app learns its port from a file it reads (see `files`).

```yaml
ports:
  http: any                       # any free port, different per workspace
  admin: 5080                     # this port, always
  web: [8000, 8110-8115, 9000]    # one free port from these
  grpc: { host: any, scheme: http }
```

The value says where on this machine the port may be. A map says more: `host` (the same any / number / list), `container` (the port inside a container, required for containers and compose services) and `scheme`. A port named `http` or `https` has that scheme; any other has none unless it says so, and so has no url.

## Repo file

```yaml
schema: 1
repo: shop-services

copy-into-worktrees: [backend/.env]

compose:
  docker-compose.yml:
    services:
      postgres:
        ports: { tcp: { container: 5432 } }
        health: pg_isready -h 127.0.0.1 -U shop
        role: database
    set:
      services.postgres.container_name: shop-${workspace.name}

containers:
  mail: { preset: mailpit }
  cache:
    image: redis:7
    ports: { tcp: { container: 6379 } }
    env: { REDIS_ARGS: --save "" }
    volumes: { data: /data }
    health: redis-cli ping

inputs:
  mail-smtp:
    type: url
    default: ${mail.smtp-url}

apps:
  migrate:
    run: dotnet
    path: tools/Migrate
    once: true
    retry: 3
    wait-for: [postgres]
  catalog-api:
    run: dotnet
    path: src/Catalog.Api
    ports: { http: any }
    health: /health
    restart: on-failure
    wait-for: [postgres, migrate]
    links: { Swagger: /swagger }
    values:
      swagger: ${catalog-api.url}/swagger

files:
  src/Catalog.Api/Properties/launchSettings.json:
    read-by: [catalog-api]
    values:
      Urls: ${catalog-api.url}

outputs:
  catalog-url:
    type: url
    value: ${catalog-api.url}

groups:
  api: [catalog-api]
```

### `apps`

| Key | |
|---|---|
| `run` | `dotnet`, `node` or `command`. `dotnet` runs `dotnet run`; `node` runs the package's `dev` script with npm or pnpm, installing packages first when they're missing or stale. |
| `path` | The app's folder. |
| `command` | Required for `run: command`; for the others, replaces the usual command. Literal. |
| `ports` | See Ports. A job has none. |
| `health` | A path (`/health`) on its first http(s) port that answers with success once it's ready. |
| `ready-log` | A regular expression; ready once a line of its output matches. |
| `restart` | `never` (default), `on-failure` or `always`. |
| `once` | `true` for a job: it runs to completion each time the apps start, and what waits for it waits for success. |
| `retry` | For a job: tries again this many times after a failure. |
| `wait-for` | Names of apps, jobs, containers and compose services to wait for. |
| `links` | Label → a path on its first http(s) port, a full url, or a value with references. |
| `role` | `web`, `api` or `worker`, where sprig can't tell. |
| `values` | Values of its own, referenced as `${app.name}`. |

### `containers`

`image` or `preset` (`postgres`, `redis`, `rabbitmq`, `mailpit`), `ports`, `env`, `volumes` (name → path inside), `health` (a command run inside), `role`, `values`. A preset fills in the image, ports, health and role, and anything written beside it is added or replaces the preset's.

| Preset | Ports | Values |
|---|---|---|
| `postgres` | `tcp` | `user`, `password` |
| `redis` | `tcp` | `address` (`localhost:port`) |
| `rabbitmq` | `amqp`, `ui` (http) | `user`, `password`, `amqp-url` |
| `mailpit` | `smtp`, `ui` (http) | `smtp-url` |

### `compose`

Compose files the repo already has, by path. Under each, `services` names the services sprig runs with ports of its own (`ports`, `health`, `role`, `values`), and `set` changes any other value in the file by a dotted path.

### `inputs` and `outputs`

An input is a value the repo can be given: by the workspace, an orchestration, or another repo's output of the same name. `type` is `url`, `connection-string`, `host-port`, `secret` or `text` (the default). `default` is used when nothing gives one; without one, sprig asks when a workspace is made. `role` says what it points at, for how it's drawn.

An output is a value the repo gives to others: `type` and `value`.

### `files`

Every file sprig writes values into, by path. Each is declared once.

| Key | |
|---|---|
| `read-by` | The apps that read it. |
| `values` | What sprig writes into it. |
| `format` | Only when the name doesn't say: `dotenv`, `json` or `launch-settings`. |
| `profile` | For `launch-settings`: the profile's name, `sprig` by default. |

| Format | Taken from the name | What sprig writes |
|---|---|---|
| `dotenv` | `.env`, `.env.*`, `*.env` | A marked block of `KEY=value` at the top and the bottom, so its values win whichever way the file is read. Everything else in the file is left alone. Flat keys only. |
| `launch-settings` | `launchSettings.json` | One profile, first, so `dotnet run` and IDEs use it. Its env starts from the profile it displaced. Flat keys only. |
| `json` | any other `.json` | Only the values named, nested as written: `ConnectionStrings: { Db: … }`. A value it takes over is kept in a comment above it and put back when the workspace goes. Sections and arrays are never replaced. |

### `groups` and `copy-into-worktrees`

A group is a slice of the apps to start on its own; starting it starts what they wait for too. `copy-into-worktrees` lists files git doesn't have (a `.env` of secrets) to copy from the repo's own checkout into each worktree.

## Orchestration file

```yaml
schema: 1
orchestration: storefront

repos:
  shop-services: { path: repos/shop-services }
  shop-web: { use: shop-web }        # by its ID on the Repos list

containers:
  mail: { preset: mailpit }

inputs:                              # a value for every repo's input of that name
  mail-smtp: ${mail.smtp-url}
  catalog-url: ${shop-services.outputs.catalog-url}

groups:
  web: [shop-web, shop-services]
  mobile: [shop-mobile, shop-services/catalog-api]

links:
  Inbox: ${mail.ui.url}
```
