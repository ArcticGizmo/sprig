<h1 align="center">Sprig</h1>
<p align="center">
 <img src="./landing-icon.png" width="150"  />
</p>

<p align="center">
<strong>Self-describing repos. One map of how they fit. Check out any slice, fully wired.</strong>
</p>

<br>

sprig lets you spin up an isolated copy of one or more repos — each on its own git
_worktree_, its own branch, its own non-colliding ports, and its own docker infrastructure —
so you can work on several things in parallel without them stepping on each other. Your source
repos are never mutated; everything sprig generates lives in a central store or in the
throwaway worktree.

- **Isolated worktrees** — each workspace gets a `<repo>--<workspace>` worktree on the branch you
  name at checkout, cut from each repo's base. Your main checkout is untouched.
- **Pooled, labelled workspaces** — a map backs a bounded pool of reusable workspaces. Check one out
  with a branch and a label saying what it's for, release it when you're done, and the next checkout
  reuses it warm.
- **Non-colliding ports** — each repo owns its ports and they're allocated per workspace, so two
  workspaces of the same map run side by side without port clashes.
- **Isolated docker infra** — sprig generates a per-workspace compose file (project name
  `sprig-<workspace>`) so containers, networks, and volumes don't collide.
- **Self-describing repos** — a repo (and each module within it) declares what it **provides**
  (its own ports, and URLs/values built from them) and what it **needs** from others. There's no
  central place that wires values in — sprig derives the wiring by matching capability names.
- **Maps, not stacks** — a _map_ is an open graph of repos you take slices of. It stores only the
  _deviations_ from automatic wiring (which provider wins when several could; a manual fallback for a
  need whose provider you left out). Check out any slice — one repo, or many — fully wired.
- **Monorepo-aware modules** — one repo can declare many _modules_, each a slice
  (`apps/web`, `apps/api`) with its own `.env` files, compose files, setup commands, and its own
  provides/needs under its own `path`. Sibling modules wire to each other locally; unmet needs bubble
  up to the map.
- **Partial workspaces** — take just the repos you need this time (`--only`/`--without`). Their
  worktrees, env and compose are never generated; a need whose provider you left out is filled by a
  map default, or reported as a gap.
- **Drift-safe** — `reconcile`/`doctor` detects and repairs record-vs-reality drift (a deleted
  or orphaned worktree), so a half-cleaned-up workspace is always recoverable.

## Installing

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/sprig/main/install.ps1 | iex
```

That's the whole install. No admin rights (it lands in `%LocalAppData%\Sprig\`), a Start Menu shortcut
and a normal uninstaller in Settings → Apps, and Sprig opens when it's done. It also drops the `sprig`
CLI on your **PATH** — open a new terminal and run `sprig --help`. Every update after this is in-app:
**About → Check for updates**.

What the script does, in order: resolves the latest release, fetches `SHA256SUMS.txt` and
`Sprig-win-Setup.exe`, **checks the installer against the manifest and deletes it rather than run it on
any mismatch**, then hands off to the installer. It's [`install.ps1`](install.ps1) in this repo — read it
before piping it into your shell, the same as you should with any installer.

Because PowerShell rather than a browser does the downloading, nothing is tagged with the mark-of-the-web —
so this route never hits the **"Windows protected your PC"** SmartScreen wall.

Pin a version instead of taking the latest:

```powershell
$env:SPRIG_VERSION = '0.4.0'; irm https://raw.githubusercontent.com/ArcticGizmo/sprig/main/install.ps1 | iex
```

### Installer by hand

Prefer to click things: download `Sprig-win-Setup.exe` from the
[latest release](https://github.com/ArcticGizmo/sprig/releases/latest) and run it. Identical install,
identical self-updates.

A browser download *is* tagged with the mark-of-the-web, so SmartScreen shows the blue **"Windows protected
your PC"** dialog — click **More info → Run anyway**, or use the one-liner above and skip it. To check the
download against the release's `SHA256SUMS.txt` yourself:

```powershell
$want = (Select-String -Path SHA256SUMS.txt -Pattern 'Sprig-win-Setup.exe').Line.Split()[0]
(Get-FileHash Sprig-win-Setup.exe -Algorithm SHA256).Hash -eq $want   # True
```

## The model in one picture

```
Repo (.sprig.json, committed in the repo)      Map (central store, never in a repo)
  = SELF-DESCRIBING                               = A SLICE + ITS DEVIATIONS
  one or more MODULES, each with                  lists the repos in play, and stores only:
    its own env/compose/setup under a path          - wiring:   who wins when >1 provider
    PROVIDES  its ports + values built from them    - defaults: a literal for a need whose
    NEEDS     capabilities from other repos                     provider isn't in the slice
  templates reference                            everything else — which provider satisfies
  ${sprig.<capability>.<output>} and               each need — is DERIVED by capability name
  ${sprig.workspace}                               at checkout.
```

Wiring is derived, not declared: a `need` is matched to a `provide` of the same **capability name**
— a sibling module first (nearest-wins), else a single provider across the slice. More than one
provider is an ambiguity the map resolves; none is a gap a map default fills, or an unmet need that
stops the checkout with an explanation.

A repo owns only its own ports. A consumer never sees a provider's port; it reads the finished value
the provider derives (`${sprig.api.url}`), so each workspace's front end talks to that workspace's own
API.

## Quick start (desktop app)

1. **Repos** → *Add repo*, point it at a git repo. If it has no `.sprig.json`, sprig asks whether it's
   one module or many; for a monorepo, name each module and its subdirectory, and sprig proposes what
   each one provides and needs. Not ready to commit a config? Add it as a virtual repo.
2. **Maps** → *New map*, pick the repos that belong together. Wiring is derived from their provides/needs.
3. **Workspaces** → check one out from the map, name its branch and label it, and sprig lays down the
   worktrees, allocated ports, env files and compose files.
4. Bring the docker infra up from the workspace's detail view, work, then release it back to the pool.

## Quick start (CLI)

The terminal covers everything the app does. The installer puts `sprig` on your PATH (or run it from a
source build with `dotnet run --project src/Sprig.Cli --`). Prefer the GUI for something more
hands-on? `sprig open` launches the desktop app; `sprig update` installs a newer release in place.
Then:

```sh
# 1. Register the repos you want to isolate (reads each repo's committed .sprig.json)
sprig repo add C:\code\my-frontend
sprig repo add C:\code\my-api

# 2. Import a map that composes them (or author it on the app's Maps page)
#    { "schema": 1, "name": "web+api", "repos": ["my-frontend", "my-api"] }
sprig map import web+api.json

# 3. Check a workspace out of the map's pool, on a branch, saying what it's for
sprig pool checkout web+api --branch feature-x --label "Risk matrix overrides"

# 4. Bring its docker infra up, work, then hand it back
sprig ws up web+api-1
sprig ws urls web+api-1                       # the entry points, e.g. http://localhost:8103
sprig pool release --workspace web+api-1      # stops containers; worktrees, deps and branch stay

# Outside a pool: a single repo, or a slice of a map
sprig ws create quick --repo C:\code\my-api
sprig ws create api-only --map web+api --without my-frontend
sprig ws rm api-only --yes                    # tears down; add --force to also delete the branch
```

Those commands spell everything out, but you rarely have to. Run a command bare at a terminal —
`sprig pool checkout`, `sprig ws rm`, `sprig ws up`, `sprig ws info`, … — and it walks you through the
choices; give it the arguments (or run it in a script, a pipe, or CI) and it goes straight through
without prompting. `--ni` forces the non-interactive path, `--json` (on any read command) gives machine
output, and `sprig ws rm api-only` at a terminal just asks to confirm rather than needing `--yes`.

Jump into a workspace: `sprig cd web+api-1` opens a new terminal already sitting in the repo (pick the
repo/module interactively, or name them), while `sprig path web+api-1` just prints the directory — the
one to use in scripts, e.g. `Set-Location (sprig path web+api-1)`. From inside one, `sprig here` says
which workspace you're in and what it's for. Using Claude Code? The [plugin](plugins/sprig) drives all of
this from a session.

Don't have a `.sprig.json` yet? `sprig init --repo C:\code\my-api --print` inspects the repo and
proposes one (a single default module; the app's *Add repo* flow is the way to split a monorepo into
several).

## Where state lives

| Location                                         | Contents                                                                                                                      |
| ------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------- |
| **Source repo** (tracked)                        | `.sprig.json` only — the single file sprig adds to your repo                                                                  |
| **Worktree** `<repo>--<workspace>` (sibling dir) | clobbered `.env.*` files (per module, under its `path`); dies with the worktree. With `worktreeLayout: claude` the worktree lives in `<repo>/.claude/worktrees/<workspace>` and the sibling path is a junction to it |
| **Central store** `%LOCALAPPDATA%\sprig`         | repo registry, virtual repo configs, maps, per-workspace records, allocated ports, generated compose files, user settings |

## Docs

- **[Configuration reference](docs/config-reference.md)** — the `.sprig.json` (schema 1: modules, provides, needs) and map schemas.
- **[Pooled workflow](docs/pooled-workflow.md)** — checking out, releasing and reusing workspaces.
- **[User guide](docs/user-guide.md)** — end-to-end walkthrough for the UI and the CLI.
- **[Packaging & updates](docs/packaging.md)** — building the installer, the checksum manifest, and the update flow.
- **[Changelog](CHANGELOG.md)** — what changed, release by release.

## Build from source

```sh
dotnet build            # build everything
dotnet test             # run the suite
dotnet run --project src/Sprig.App    # launch the desktop app from the build output
dotnet run --project src/Sprig.Cli -- --help    # the CLI
```

To try a change against small, disposable repos instead of your real ones, build the examples sandbox with `scripts\sandbox` (cmd) or `.\scripts\sandbox.ps1` (PowerShell). See [examples/README.md](examples/README.md).

## Requirements

- .NET 10 SDK (to build; the installed app is self-contained and needs no runtime)
- git (on `PATH`)
- Docker Desktop / docker compose — only needed for workspaces whose repos declare infra.

Windows-first; the engine (`Sprig.Core`) is kept OS-agnostic.
