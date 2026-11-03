# Handover: experimental branch

Written 2026-11-03 at the end of the session that did M4. Read this first, then [experimental-roadmap.md](experimental-roadmap.md).

## Where things stand

- Branch `experimental`. Nothing pushed. `main` is untouched.
- M0 to M4 are done. **M5 (runners) is next.**
- All tests pass (`dotnet test sprig.slnx`, about 90 s; integration tests compile real definitions with `dotnet run`, and the sprout tests make real git repos in `%TEMP%`).
- Warnings are errors repo-wide (`Directory.Build.props`). Build with normal verbosity: `-v q` hides warnings.
- Version is `0.100.0-exp`. **Never tag from this branch**: the release workflow publishes on any `v*` tag. Don't use the `bump-version` or `release-next-version` skills here.

## Decisions already made (don't reopen)

| Topic | Decision |
|---|---|
| Authoring | C# file-based apps. Each repo has a `sprig.cs` (`Repo.Describe`); a trellis file `<name>.trellis.cs` (`Trellis.Describe`) joins repos. |
| Names | **Trellis** (the file joining repos) and **sprout** (one running copy). Not "system", "composition", "map" or "workspace". |
| Trellis location | Any folder, registered once with `sprig trellis add <file>`. |
| Editing | External IDE first (IntelliSense confirmed in VS Code/Rider via `#:package Sprig.Sdk`). The in-app editor works but needs style polish (in Later). |
| Infra | Code presets (`Postgres`, `Redis`, `RabbitMq`, `Mailpit`), and a repo's own compose file is allowed (`repo.ComposeFile(...).Service(...).Endpoint(...).Health(...)`, `.Set(path, value)`). |
| Env values | `.env` files first. Language-specific outputs are opt-in per app: `.Output(EnvOutput.LaunchSettings())`, `EnvOutput.AppSettings(file)`, `EnvOutput.DotEnv(file)`. |
| Files sprig writes | **The repo opts in.** A target must be a file the app reads and the repo's `.gitignore` covers. Tracked or not-ignored targets are refused (by `plan` and before `up` creates anything) with what to add. Sprig never adds ignore rules itself, so no `info/exclude`: it's shared by every worktree and hides the problem. Keep this for every new output. |
| Unnamed compose services | Left out of sprouts, with a warning from `sprig plan` (services behind a compose profile don't count). |
| Run experience | Log pane per runner in the app (M5). Telemetry later. |
| Who runs apps | **A background `sprig-host`**, one per store, started on demand by the CLI or app, over a current-user-only named pipe. Apps outlive the CLI and the window; each runner is in a Job Object, so a dying host takes its apps with it. |
| UI testing | The user tests the UI by hand only once most of the structure is done. Verify with headless renders and view-model tests meanwhile; don't ask them to try pages. |
| Store | `%LOCALAPPDATA%\sprig-exp` (`sprig-exp (Dev)` for Debug). Never the released app's `sprig` folder; a test guards it. |

## How the pieces fit

```
repo/sprig.cs ──┐                         dotnet run, scrubbed env, time limit,
repo/sprig.cs ──┼─ DefinitionRunner ──►  trusted by SHA-256 of content, cached by sha + SDK version + schema
x.trellis.cs ───┘   (Engine/Definitions)   → DefinitionModel JSON (RepoModel | TrellisModel), schema 2
                         │
                  TrellisLoader (Engine/Trellises) — evaluates trellis + repos in parallel; RepoDirs; ComposeFiles()
                         │
                  Planner (Engine/Planning) — scopes, imports/exports, ports (kept leases first), {templates}
                         │
                  SproutService (Engine/Sprouts) — Up / Down / Remove / List / LastPlan
                     ├─ GitClient (Engine/Git): worktrees, IsTracked, IsIgnored (CheckFiles uses both)
                     ├─ PortLeases: <store>/ports.json under a lock file, keyed "<trellis>/<sprout>" → "<owner>/<resource>.<endpoint>"
                     ├─ EnvFiles: dotenv marker blocks (top and bottom), launch profile, appsettings keys; each removable
                     ├─ ComposeYaml: ForContainers (code presets) and ForSprout (a repo's file, trimmed and re-ported)
                     ├─ DockerCompose: up --wait, down [--volumes]
                     └─ SproutStore: <store>/sprouts/<trellis>/<sprout>/{sprout.json, plan.json, *.compose.yml}

RunnerSpecs (plan → what to run) ──► HostClient ══ named pipe ══► sprig-host (src/Sprig.Host)
                                                                    HostServer → Supervisor → Runner per app
                                                                    (build per repo, Job Object, log to <store>/logs, ready on port)
```

- **Projects:** `Sprig.Sdk` (what definitions reference; `Model.cs` is the contract, `Sdk.Schema` its version), `Sprig.Engine`, `Sprig.LanguageHost`, `Sprig.App` (Avalonia 12), `Sprig.Cli` (Spectre), `tests/Sprig.Tests`.
- **CLI:** `trellis add|ls|rm`, `trust add|ls`, `plan`, `up`, `down`, `rm`, `sprouts`, `env`, `start`, `stop`, `ps`, `logs`, `host status|stop`.
- **App:** Trellises page (plan preview), Sprouts page (new sprout form, list, details, up/stop/remove), `OperationProgressWindow` fed by `SproutEvent`s.
- **A sprout's record** says exactly what `up` did (worktrees, whether it made the branch, files and whether it created them, compose projects), and `down`/`rm` undo only that.
- **Port leases:** stable keeps a sprout's ports until `rm`; random gets fresh ones on each `up` from down. A sprout that's up doesn't count its own listening ports as busy.

## Testbed

- Templates in `testbed/repos/{shop-services,shop-web,shop-mobile}` plus `testbed/storefront.trellis.cs` (worktrees) and `testbed/services.trellis.cs` (the monorepo in place).
- `catalog-api` gets its values from a launch profile and `orders-api` from `appsettings.Local.json`, which its `Program.cs` loads. Both are in `shop-services/.gitignore`.
- `pwsh -File scripts/testbed.ps1 [-Path <dir>] [-Force] [-UsePackage]` builds real git repos (default `../sprig-testbed`). Use `C:\Users\JonHowell\AppData\Local\Temp\sprig-tb-m4` or similar for throwaway ones.
- Try it with a throwaway store: `$env:SPRIG_STORE='...'; sprig trellis add <tb>/storefront.trellis.cs --trust; sprig up storefront --sprout feat-a; sprig up storefront --sprout feat-b`. Then `dotnet run` in each worktree's `src/Catalog.Api` and open `/config`.
- Each app has a config page (`/config` on the APIs, a Config tab on the front ends) showing each value, its source and whether its target answers. That's how M5 should prove wiring.
- Headless renders, no screen capture: `sprig-gui render-sprouts <page.png> <progress.png>` (uses `SPRIG_STORE`), `render-trellises`, `render-editor`.

## Gotchas found the hard way

- **ASP.NET Core urls:** a `urls` key in `appsettings.json` beats `ASPNETCORE_URLS`. Give .NET apps their url in `Urls` (`.Http(urlEnv: "Urls")`); the launch profile's `applicationUrl` follows it.
- **Parallel `dotnet run` in one worktree** races on shared projects' `obj` (CS2012). Runners build once per repo (a lock per repo folder), then run with `--no-build --no-launch-profile`.
- **Starting a background process from the CLI:** start it with shell-execute, or it inherits the CLI's stdout and anything piping the CLI's output hangs until the background process exits.
- **The host holds each app's log open**, so read logs with `FileShare.ReadWrite` (`File.ReadAllText` fails).
- **Windows 8.3 short paths:** `%TEMP%` here is `C:\Users\JONHOW~1\…`. Vite returns 403 from such a path. Always pass paths through `Engine/Store/LongPath.Of`.
- **This machine already runs containers** on 5432, 8004–8006, 8012–8014 and others, and the released sprig leaves `sprig-feature-*` volumes. Never stop or remove containers or volumes you didn't start; sprout projects are all `sprig-<trellis>-<sprout>[-<repo>]`.
- **Compose without health checks:** `--wait` only means "running". Postgres and friends need a check (presets have one; repo compose services take `.Health(...)`).
- **Avalonia styles:** a local `Foreground` beats a class style, so state colours (`.up`, `.bad`) must come from styles, never a local value on the same element.
- **`grep -r` + `sed -i` across `src/` hits `bin/` binaries.** Exclude build output.
- **Stopping processes:** the harness blocks `taskkill /PID …`. Use `Stop-Process` and walk children with `Get-CimInstance Win32_Process`.
- **The SDK's `Sprig.Text` value type shadows `Spectre.Console.Text`** inside `Sprig.*` namespaces; the CLI aliases it as `SpectreText`.
- **A referenced exe project** copies a bare `.exe` stub into referencing projects; `LanguageHostClient` only accepts a host with its `.dll` beside it.
- File-based apps default to Native AOT settings, so the SDK uses source-generated JSON (`ModelJsonContext`, polymorphic on `kind`).
- Roslyn needs the `FileBasedProgram` parse feature or every `#:` line is an error.
- Front-end packages are pinned to releases at least two weeks old. Install with `npm ci --ignore-scripts`.

## User conventions to keep

- Path fields use `Controls/PathBox` (folder button, Tab completion, forward slashes, including when displaying stored paths).
- Ask before taking screenshots of non-desktop UI. Desktop captures and headless renders go in `captures/{timestamp}-{reason}.png` and are never deleted.
- No private repo or project names in docs, artefacts or examples. Use the testbed names (shop, catalog, orders).
- Code comments explain why, never what the code used to do. Markdown has no hard-wrapped sentences.
- Commit messages end with the `Co-Authored-By` line. Commit when asked; the user said to commit milestone work as it lands.

## M5 in short (see the roadmap for the full list)

The host spike is done: `sprig-host`, Job Objects, per-repo builds, port readiness, logs, and `sprig start|stop|ps|logs`. Next: `WaitFor` ordering and one-off jobs (`repo.Job` for `Migrate`, with `After`), a graceful stop before the kill, HTTP and log-line readiness, restart policy, groups, `sprig up --run` with interleaved output, the app's runner list and log pane (fed from the host), and the IDE handoff (stop one runner, its port stays leased). Done when one command brings up infra, runs `migrate`, starts both APIs once Postgres and RabbitMQ are ready, then web and mobile.

Open question for M5 or later: a sprout evaluates its repos' definitions from their main checkouts, not its worktrees, because trust is per path. Evaluating worktrees needs a rule for how approval carries over (for example, trust by content hash within a registered repo's worktrees).
