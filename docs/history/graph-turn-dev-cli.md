# Dev CLI — testing the map model (the Graph Turn)

A runbook for exercising the **experimental `map` commands** on the `graph-turn` branch **from a
source build**. The `sprig` on your PATH is the *released* build and does **not** have these commands —
you must run the dev exe below.

> Everything here is **additive**: your existing `stack`/`pool` workflow is untouched, and every
> command runs against a **throwaway store** so your real sprig data (workspaces, ports, repos) is
> never touched.

---

## 1. One-time setup (PowerShell)

Run from the repo root. Rebuild after any code change (the exe is a compiled snapshot).

```powershell
# Build the dev CLI once (fast; re-run after editing Sprig.Core/Sprig.Cli)
dotnet build src/Sprig.Cli/Sprig.Cli.csproj

# Point sprig at a throwaway store so nothing here touches your real data.
$env:SPRIG_STORE = "$env:TEMP\sprig-dev-store"

# `sd` = "sprig dev": the freshly-built exe, with the new `map` commands.
$SprigExe = "$PWD\src\Sprig.Cli\bin\Debug\net10.0\sprig.exe"
function sd { & $SprigExe @args }

sd --help          # sanity check — you should see a `map` branch in the list
```

After changing code: `dotnet build src/Sprig.Cli/Sprig.Cli.csproj` again, then keep using `sd`.
(Prefer no build step? `function sd { dotnet run --project src/Sprig.Cli -- @args }` — slower, always
fresh.)

**Reset the sandbox** any time to start clean:

```powershell
Remove-Item -Recurse -Force $env:SPRIG_STORE -ErrorAction SilentlyContinue
```

---

## 2. Quickstart — a monorepo with local wiring

This mirrors the model's headline: a repo's `web` module **needs** what its `api` module **provides**,
wired automatically with no map bindings.

```powershell
# A scratch monorepo (api provides a port + derived url; web consumes the url).
$Repo = "$env:TEMP\acme"
New-Item -ItemType Directory -Force "$Repo\apps\api", "$Repo\apps\web" | Out-Null
@'
{ "schema": 1, "name": "acme", "modules": [
  { "name": "api", "path": "apps/api",
    "provides": [ { "capability": "acme-api",
      "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.acme-api.port}" } } ],
    "env": [ { "file": ".env", "set": { "PORT": "${sprig.acme-api.port}" } } ] },
  { "name": "web", "path": "apps/web",
    "needs": [ { "capability": "acme-api" } ],
    "env": [ { "file": ".env", "set": { "API": "${sprig.acme-api.url}" } } ] } ] }
'@ | Set-Content "$Repo\.sprig.json" -Encoding utf8
git -C $Repo init -q; git -C $Repo add -A; git -C $Repo -c user.email=t@t -c user.name=t commit -qm init

# Register it, then author a map (just a list of repos) and import it.
sd repo add $Repo
'{ "Schema": 1, "Name": "dev", "Repos": [ "acme" ] }' | Set-Content "$env:TEMP\dev.map.json" -Encoding utf8
sd map import "$env:TEMP\dev.map.json"

sd map ls
sd map show dev

# Grow the workspace — allocates ports, resolves the wiring, lays down worktrees + env.
sd map create feat-1 --map dev
sd ws info feat-1

# See the resolved wiring: web's .env points at the api's allocated port.
Get-Content "$Repo\acme--feat-1\apps\web\.env"     # -> API=http://localhost:8000
Get-Content "$Repo\acme--feat-1\apps\api\.env"     # -> PORT=8000
```

Tear it down like any workspace (map workspaces reuse the normal lifecycle):

```powershell
sd ws rm feat-1 --yes          # add --force to also delete any claim branch
```

---

## 3. Things worth trying

**Propose a config for a repo** (infers `provides` from detected ports):

```powershell
sd init --map --repo $Repo --print
```

**Partial selection** — leave a repo out; if a remaining repo *needs* a left-out provider you'll get a
precise gap, otherwise it just checks out the slice:

```powershell
sd map create api-only --map dev --without web
```

**An unmet need is reported, not a crash.** Point a `need` at something no selected repo provides and
`map create` fails with a named gap (`… needs 'x' — add the provider or supply a value`) and rolls
everything back.

**Map deviations** — when several repos provide the same capability, or a provider isn't selected, the
map carries only those exceptions:

```jsonc
{
  "Schema": 1, "Name": "dev", "Repos": [ "acme", "web-fork" ],
  // pick which provider satisfies a need when >1 match:  [repo][capability] = providerCapability
  "Wiring":   { "web": { "http-api": "acme-api" } },
  // supply a value for a need whose provider you didn't select:  [repo][capability][output] = literal
  "Defaults": { "web": { "auth": { "url": "https://auth.staging.example.com" } } }
}
```

**Git-URL bootstrap** — a map repo entry can carry a URL; on checkout sprig clones + registers it under
`$SPRIG_STORE\repos\<name>` if it isn't already known:

```jsonc
{ "Schema": 1, "Name": "dev", "Repos": [ "acme", { "name": "billing", "repo": "https://github.com/you/billing.git" } ] }
```

---

## 4. Command reference (the new surface)

| Command | What it does |
|---|---|
| `sd init --map [--repo <path>] --print` | Propose a `.sprig.json` in the map model (provides/needs) |
| `sd map ls` | List defined maps |
| `sd map show <name>` | Show a map's repos, wiring, and defaults |
| `sd map import <file.json>` | Validate + save a map definition |
| `sd map create <ws> --map <name> [--without a,b] [--from <ref>]` | Grow a workspace from a map slice |
| `sd ws info \| ls \| up \| down \| status \| rm` | The normal workspace verbs — work on map workspaces too |

Map files live at `$env:SPRIG_STORE\maps\<name>.json`. A map is just `Repos` plus the *deviations*
(`Wiring`, `Defaults`); everything else is derived from the repos' own `provides`/`needs`.

Repo config (`.sprig.json`) shape in the map model:

```jsonc
{
  "schema": 3,                        // transitional; becomes 1 when stacks are retired (M7)
  "name": "acme",
  "provides": [                       // single-app sugar; a monorepo uses "modules" instead
    { "capability": "acme-api",
      "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.acme-api.port}" } }
  ],
  "needs": [ { "capability": "acme-db", "as": "db" } ],
  "env": [ { "file": ".env", "set": { "PORT": "${sprig.acme-api.port}", "DB": "${sprig.db.connString}" } } ]
}
```

---

## 5. Not wired yet (coming in M7)

Pooled checkout (`pool`) and workspace **refresh** don't yet work for *map* workspaces — those paths
still use the old stack-era scope and are rebuilt when stacks are retired. **Create / info / up / down
/ status / rm all work today.**

---

## Appendix — Git Bash equivalent

```bash
dotnet build src/Sprig.Cli/Sprig.Cli.csproj
export SPRIG_STORE="$(mktemp -d)/store"
sd() { "$PWD/src/Sprig.Cli/bin/Debug/net10.0/sprig.exe" "$@"; }
sd --help
```
