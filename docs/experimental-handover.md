# Handover: experimental branch

Written 2026-11-03, after the session that finished M5 (readiness checks, restart policies, `up --run`, the app's runners and log pane). Read this first, then [experimental-roadmap.md](experimental-roadmap.md), which has each milestone's outcome and findings.

## Where things stand

- Branch `experimental`, pushed to `origin/experimental` (tracking). `main` is untouched.
- M0 to M5 are done. M6 is next.
- 174 tests pass (`dotnet test sprig.slnx`, about 60 s). The old intermittent host-test failures were the host dying from its own Ctrl+C (see Gotchas); after the fix, 5 full runs and 20 runs of the stop tests were clean. If a host test fails, the Application event log (ids 1000 and 1026) shows whether `sprig-host` crashed. Integration tests compile real definitions with `dotnet run`, the sprout tests make real git repos in `%TEMP%`, and the host tests start the real `sprig-host`.
- Warnings are errors repo-wide (`Directory.Build.props`). Build with normal verbosity: `-v q` hides warnings.
- Version is `0.100.0-exp`. **Never tag from this branch**: the release workflow publishes on any `v*` tag. Don't use the `bump-version` or `release-next-version` skills here.
- The user asked to commit, push and continue as each piece lands.

## Next

M5 is done; see the roadmap's M5 section for everything in it.

1. **M6 (review against Aspire).** The open question at the end of this file needs an answer first.
2. The user hasn't yet tried the app's new runner rows and log pane by hand (renders are in `captures/`).

## Decisions already made (don't reopen)

| Topic | Decision |
|---|---|
| Authoring | C# file-based apps. Each repo has a `sprig.cs` (`Repo.Describe`); a trellis file `<name>.trellis.cs` (`Trellis.Describe`) joins repos. |
| Names | **Trellis** (the file joining repos) and **sprout** (one running copy). Not "system", "composition", "map" or "workspace". |
| Trellis location | Any folder, registered once with `sprig trellis add <file>`. |
| Editing | External IDE first. The in-app editor works but needs style polish (in Later). |
| Infra | Code presets (`Postgres`, `Redis`, `RabbitMq`, `Mailpit`, each with a health check), and a repo's own compose file (`repo.ComposeFile(...).Service(...).Endpoint(...).Health(...)`, `.Set(path, value)`). |
| Env values | `.env` files first. Language-specific outputs are opt-in per app: `.Output(EnvOutput.LaunchSettings())`, `EnvOutput.AppSettings(file)`, `EnvOutput.DotEnv(file)` (defaults to `.env.local`). |
| Files sprig writes | **The repo opts in.** A target must be a file the app reads and the repo's `.gitignore` covers. Tracked or not-ignored targets are refused (by `plan` and before `up` creates anything), with what to add. Sprig never adds ignore rules itself, so no `info/exclude`: every worktree shares it and it hides the problem. Keep this for every new output. |
| Unnamed compose services | Left out of sprouts, with a warning from `sprig plan` (services behind a compose profile don't count). |
| Who runs apps | **A background `sprig-host`**, one per store, started on demand by the CLI or app, over a current-user-only named pipe. It exits after 10 idle minutes. Apps outlive the CLI and the window; each runner is in a Job Object, so a dying host takes its apps with it. |
| Ordering | `WaitFor` means ready for an app, finished with code 0 for a job, and healthy for infra. There's no separate `After`. Apps also start after apps their values point at, unless that closes a loop. |
| Stopping | Ctrl+C to the app's console, then end the job after 10 s if a real program is still running. |
| `up --run` | (User, 2026-11-03.) Follows every app's log, `docker compose up` style. Ctrl+C in the CLI stops the apps (the infra stays up for `sprig down`); a second Ctrl+C leaves at once and the host finishes the stops. `-d/--detached` starts the apps, waits until they're ready and returns. Plain `up` still does only the infra. |
| UI testing | The user tests the UI by hand only once most of the structure is done. Verify with headless renders and view-model tests meanwhile; don't ask them to try pages. |
| Store | `%LOCALAPPDATA%\sprig-exp` (`sprig-exp (Dev)` for Debug). Never the released app's `sprig` folder; a test guards it. |

## How the pieces fit

```
repo/sprig.cs ──┐                         dotnet run, scrubbed env, time limit,
repo/sprig.cs ──┼─ DefinitionRunner ──►  trusted by SHA-256 of content, cached by sha + SDK version + schema
x.trellis.cs ───┘   (Engine/Definitions)   → DefinitionModel JSON (RepoModel | TrellisModel), schema 4
                         │
                  TrellisLoader (Engine/Trellises) — evaluates trellis + repos in parallel; RepoDirs; ComposeFiles()
                         │
                  Planner (Engine/Planning) — scopes, imports/exports, ports (kept leases first), {templates},
                         │                    PlannedApp.DependsOn (WaitFor + value-implied, loop-free)
                  SproutService (Engine/Sprouts) — Up / Down / Remove / List / LastPlan / CheckFiles
                     ├─ GitClient (Engine/Git): worktrees, IsTracked, IsIgnored
                     ├─ PortLeases: <store>/ports.json under a lock file, "<trellis>/<sprout>" → "<owner>/<resource>.<endpoint>"
                     ├─ EnvFiles: dotenv marker blocks (top and bottom), launch profile, appsettings keys; each removable
                     ├─ ComposeYaml: ForContainers (code presets) and ForSprout (a repo's file, trimmed and re-ported)
                     ├─ DockerCompose: up --wait, down [--volumes]
                     ├─ SproutStore: <store>/sprouts/<trellis>/<sprout>/{sprout.json, plan.json, *.compose.yml}
                     └─ HostClient.StopAndWait before down/rm

RunnerSpecs (plan → specs + dependency closure) ──► HostClient ══ named pipe ══► sprig-host (src/Sprig.Host)
                                                       HostServer → Supervisor → Runner per app/job:
                                                       wait for previous copy → build (lock per repo) → wait for deps
                                                       → start in a JobObject → ready when its log line matches and
                                                         health urls answer 2xx (else its port opens) / job exit 0
                                                       → on its own exit: restart with backoff if its policy says so
                                                       stop: Ctrl+C via a `sprig-host --ctrl-c <pid>` helper →
                                                         "Y" to cmd → kill after 10 s
                                                       logs: <store>/logs/<trellis>/<sprout>/<repo>/<app>.log
```

- **Projects:** `Sprig.Sdk` (what definitions reference; `Model.cs` is the contract, `Sdk.Schema` its version), `Sprig.Engine`, `Sprig.Host` (`sprig-host`, a WinExe), `Sprig.LanguageHost`, `Sprig.App` (Avalonia 12), `Sprig.Cli` (Spectre), `tests/Sprig.Tests`. The CLI and app reference the two host exes with `ReferenceOutputAssembly="false"` and find them beside themselves or in the repo's `bin`.
- **CLI:** `trellis add|ls|rm`, `trust add|ls`, `plan`, `up [--run | -d]`, `down`, `rm`, `sprouts`, `env`, `start [apps] [--group g] [--no-wait]`, `stop [apps]`, `ps`, `logs <app> [-n]`, `host status|stop`. `up --run` is `AppFollow` + `LogFollower` in `Sprig.Cli/Commands`.
- **Host protocol** (`Engine/Hosting/HostProtocol.cs`): one JSON line each way per connection. Ops are `ping`, `list`, `start` (specs, plus a `restart` list), `stop` (sprout, optional name) and `shutdown`. `start` only (re)starts what isn't already going, except names in `restart`. Stopping a whole sprout forgets its runners once they've stopped.
- **App:** Trellises page (plan preview), Sprouts page (new sprout form, list, details, bring up / take down / remove, runner rows with start/restart/stop/log, and a log pane), `OperationProgressWindow` fed by `SproutEvent`s. `SproutsView` polls the host every second while the page shows; the headless renderer calls `PollAsync` itself because timers don't tick there.
- **A sprout's record** says exactly what `up` did, and `down`/`rm` undo only that. `down` and `rm` stop the sprout's apps first.

## Testbed

- Templates in `testbed/repos/{shop-services,shop-web,shop-mobile}`, plus `testbed/storefront.trellis.cs` (worktrees) and `testbed/services.trellis.cs` (the monorepo in place).
- `shop-services` has a `migrate` job (`repo.DotNetJob`) that both APIs wait for. `catalog-api` gets its values from a launch profile; `orders-api` gets them from `appsettings.Local.json`, which its `Program.cs` loads. Both files are in `shop-services/.gitignore`.
- `shop-services`' APIs are ready through `/health` and restart on failure, `migrate` retries up to 3 times, and `shop-mobile` is ready on Vite's "ready in" log line.
- `pwsh -File scripts/testbed.ps1 [-Path <dir>] [-Force] [-UsePackage]` builds real git repos. The latest throwaway one is at `%LOCALAPPDATA%\Temp\sprig-tb-m5`, with a store at `%LOCALAPPDATA%\Temp\sprig-store-m5` (its sprout was removed; the trellis is still registered). The M4 ones (`sprig-tb-m4`, `sprig-store-m4`) predate schema 4 and the readiness changes.
- The full loop with a throwaway store (`$env:SPRIG_STORE`): `sprig trellis add <tb>/storefront.trellis.cs --trust`, `sprig up storefront --sprout feat-a --trust` (the repos' `sprig.cs` files need trusting too), `sprig start storefront feat-a` (everything ready in about 23 s, in dependency order), then `sprig stop`/`down`/`rm`. Compose project names don't include the store, so use a sprout name no other store has running.
- Each app has a config page (`/config` on the APIs, a Config tab on the front ends) showing each value, its source and whether its target answers.
- Headless renders, no screen capture: `sprig-gui render-sprouts <page.png> <progress.png>` (uses `SPRIG_STORE`), `render-trellises`, `render-editor`. Save them to `captures/`.

## Gotchas found the hard way

- **Ctrl+C on Windows** (`Engine/Hosting/ConsoleSignal.cs` and `Supervisor.StopCoreAsync`); read these before changing anything there:
  - Clear the inherited "ignore Ctrl+C" flag at host start (`ConsoleSignal.Prepare`), or apps inherit it and never stop gently.
  - **The host never attaches to an app's console.** Whatever attaches gets the Ctrl+C too. The host used to attach itself and survive on a console handler, but every so often the signal killed it anyway (no crash event: it simply exited) and every app with it. That was the long-running "flaky host tests". Now `sprig-host --ctrl-c <pid>` (`ConsoleSignal.RunHelper`) attaches, sets "ignore Ctrl+C" for itself (safe: it starts nothing), sends the signal and exits. The host waits up to 10 s for it.
  - npm and vite are `.cmd` batch files: `cmd` asks "Terminate batch job (Y/N)?" and waits. The host writes "Y" to stdin after a second.
  - Mark a runner as stopping before the stop request returns, or a quick status check reads a stale "ready" as done.
  - Never run the host in-process in tests: attaching to an app's console takes the test runner's console away.
  - The stop runs fire-and-forget, so an exception in it used to leave the runner `Stopping` forever. `StopCoreAsync` now catches, logs and still ends the job. Reading a process's name can throw `InvalidOperationException` if it has just exited; catch that alongside `ArgumentException`.
  - A process starts under the runner's gate, so a stop either sees it and signals it or is seen first and nothing starts. Keep that when touching `Launch` (restarts made the window real).
- **Env files are only written when their content changes.** Vite restarts on any write to `.env.local`, and ASP.NET reloads `reloadOnChange` settings, so an `up` that changes nothing used to bounce running apps.
- **RabbitMQ sometimes fails its first start** with `.erlang.cookie: eacces` (a container-level race). Running `up` again worked.
- **Host pipe:** `HostClient.TrySend` returns null if the host drops the connection (it does after answering `shutdown`). Don't wrap the pipe in a `StreamWriter`: its dispose flushes into the closed pipe and throws.
- **Health checks and localhost:** a refused connection on Windows takes about 2 s, so the health client tries each loopback address with a 500 ms connect timeout, like the port check. It never uses a proxy.
- **Starting a background process from the CLI:** use shell-execute (`HostClient.Launch`), or it inherits the CLI's stdout and anything piping the CLI's output hangs until the background process exits.
- **The host holds each app's log open**, so read logs with `FileShare.ReadWrite` (`File.ReadAllText` fails).
- **.NET apps under runners:** `dotnet run --no-build --no-launch-profile` (the app's own profile would override sprig's values), with `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT=Development` unless the app sets them. Builds go one at a time per repo, and a restart rebuilds only after the old copy has gone (Windows locks a running app's files).
- **ASP.NET Core urls:** a `urls` key in `appsettings.json` beats `ASPNETCORE_URLS`. Give .NET apps their url in `Urls` (`.Http(urlEnv: "Urls")`).
- **Fresh worktrees have no `node_modules`.** Node runners install first with `npm ci --ignore-scripts`.
- **Windows 8.3 short paths:** `%TEMP%` here is `C:\Users\JONHOW~1\…`. Vite returns 403 from such a path. Always pass paths through `Engine/Store/LongPath.Of`.
- **This machine already runs containers** on 5432, 8004–8006, 8012–8014 and others, and the released sprig leaves `sprig-feature-*` volumes. Never stop or remove containers or volumes you didn't start; sprout projects are all `sprig-<trellis>-<sprout>[-<repo>]`.
- **Compose without health checks:** `--wait` only means "running". Postgres and friends need a check.
- **Model changes:** bump `Sdk.Schema`. The definition cache key includes it, and `DefinitionRunnerTests` reads it rather than hardcoding it.
- **Avalonia styles:** a local `Foreground` beats a class style, so state colours (`.up`, `.bad`, `.busy`) must come from styles.
- **Avalonia `ListBox.ScrollIntoView`** also scrolls every scroller above it, so inside a page's `ScrollViewer` it drags the page. Scroll the list's own `ScrollViewer` (`ScrollToEnd`) instead.
- **`grep -r` + `sed -i` across `src/` hits `bin/` binaries.** Exclude build output.
- **Stopping processes by hand:** the harness blocks `taskkill /PID …`. Use `Stop-Process` and walk children with `Get-CimInstance Win32_Process`.
- **The SDK's `Sprig.Text` value type shadows `Spectre.Console.Text`** inside `Sprig.*` namespaces; the CLI aliases it as `SpectreText`.
- **A referenced exe project** copies a bare `.exe` stub into referencing projects; the host locators only accept an exe with its `.dll` beside it.
- File-based apps default to Native AOT settings, so the SDK uses source-generated JSON (`ModelJsonContext`, polymorphic on `kind`).
- Roslyn needs the `FileBasedProgram` parse feature or every `#:` line is an error.
- Front-end packages are pinned to releases at least two weeks old. Install with `npm ci --ignore-scripts`.

## Loose ends (known, not yet done)

- **EDR:** nothing was blocked on this machine, but whether CrowdStrike or Defender raised non-blocking alerts for the host (process trees, the `--ctrl-c` helper attaching to consoles) is a question for SecOps. Worth asking before going much further.
- **Untested paths:** the host's 10-minute idle exit, and every non-Windows path (stops fall back to killing the tree, with no SIGINT yet).
- `sprig host status` reports version `1.0.0`, because `Sprig.Host` has no version set.
- Repos used with an early M4 build may still have lines under "# files sprig writes for sprouts" in `.git/info/exclude`; they're safe to delete.

## User conventions to keep

- Path fields use `Controls/PathBox` (folder button, Tab completion, forward slashes, including when displaying stored paths).
- Ask before taking screenshots of non-desktop UI. Desktop captures and headless renders go in `captures/{timestamp}-{reason}.png` and are never deleted.
- No private repo or project names in docs, artefacts or examples. Use the testbed names (shop, catalog, orders).
- Code comments explain why, never what the code used to do. Markdown has no hard-wrapped sentences.
- Commit messages end with the `Co-Authored-By` line.

## Open question (before M6)

A sprout evaluates its repos' definitions from their main checkouts, not its worktrees, because trust is per path. So a branch that changes `sprig.cs` isn't reflected until it's merged. Evaluating worktrees needs a rule for how approval carries over, for example trusting by content hash within a registered repo's worktrees.
