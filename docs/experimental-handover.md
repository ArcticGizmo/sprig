# Handover: experimental branch

Written 2026-11-03, after the session that added readiness checks and restart policies to M5. Read this first, then [experimental-roadmap.md](experimental-roadmap.md), which has each milestone's outcome and findings.

## Where things stand

- Branch `experimental`, pushed to `origin/experimental` (tracking). `main` is untouched.
- M0 to M4 are done. **M5 (runners) is mostly done**; what's left is listed under "Next" below.
- 161 tests pass (`dotnet test sprig.slnx`, about 60 s), apart from a known intermittent host-test flake (see Loose ends). Integration tests compile real definitions with `dotnet run`, the sprout tests make real git repos in `%TEMP%`, and the host tests start the real `sprig-host`.
- Warnings are errors repo-wide (`Directory.Build.props`). Build with normal verbosity: `-v q` hides warnings.
- Version is `0.100.0-exp`. **Never tag from this branch**: the release workflow publishes on any `v*` tag. Don't use the `bump-version` or `release-next-version` skills here.
- The user asked to commit, push and continue as each piece lands.

## Next

Readiness (`.Http(health:)`, `.ReadyOnLog`) and restarts (`.Restart`, `.Retry` on jobs) are done; see the roadmap's M5 section.

1. **`sprig up --run`:** `up`, then `start`, then follow every app's log interleaved with a coloured `name |` prefix, like `docker compose up`. Ctrl+C should probably stop the apps (decide, or ask the user).
2. **App runner list and log pane:** the Sprouts page shows each runner's state from the host (`HostClient` is already on `AppServices.Host`), with start and stop buttons and a log pane (follow, ANSI colour, search, virtualised). Show `Restarting` and the restart count too. Verify with headless renders and view-model tests; the user does hand testing later.
3. Then M6 (review against Aspire). The open question at the end of this file needs an answer before then.

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
                                                       stop: ConsoleSignal Ctrl+C → "Y" to cmd → kill after 10 s
                                                       logs: <store>/logs/<trellis>/<sprout>/<repo>/<app>.log
```

- **Projects:** `Sprig.Sdk` (what definitions reference; `Model.cs` is the contract, `Sdk.Schema` its version), `Sprig.Engine`, `Sprig.Host` (`sprig-host`, a WinExe), `Sprig.LanguageHost`, `Sprig.App` (Avalonia 12), `Sprig.Cli` (Spectre), `tests/Sprig.Tests`. The CLI and app reference the two host exes with `ReferenceOutputAssembly="false"` and find them beside themselves or in the repo's `bin`.
- **CLI:** `trellis add|ls|rm`, `trust add|ls`, `plan`, `up`, `down`, `rm`, `sprouts`, `env`, `start [apps] [--group g] [--no-wait]`, `stop [apps]`, `ps`, `logs <app> [-n]`, `host status|stop`.
- **Host protocol** (`Engine/Hosting/HostProtocol.cs`): one JSON line each way per connection. Ops are `ping`, `list`, `start` (specs, plus a `restart` list), `stop` (sprout, optional name) and `shutdown`. `start` only (re)starts what isn't already going, except names in `restart`. Stopping a whole sprout forgets its runners once they've stopped.
- **App:** Trellises page (plan preview), Sprouts page (new sprout form, list, details, up/stop/remove), `OperationProgressWindow` fed by `SproutEvent`s. The app doesn't show runners yet (Next, item 4).
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
  - The host is attached to the app's console while signalling, so it gets the Ctrl+C too. Add its handler on **every** attach (one from an earlier console doesn't protect it in the next) and **never remove** it (Ctrl+C can arrive late). Getting this wrong kills the host, and every app with it.
  - npm and vite are `.cmd` batch files: `cmd` asks "Terminate batch job (Y/N)?" and waits. The host writes "Y" to stdin after a second.
  - Mark a runner as stopping before the stop request returns, or a quick status check reads a stale "ready" as done.
  - Never run the host in-process in tests: attaching to an app's console takes the test runner's console away.
  - The stop runs fire-and-forget, so an exception in it used to leave the runner `Stopping` forever. `StopCoreAsync` now catches, logs and still ends the job. Reading a process's name can throw `InvalidOperationException` if it has just exited; catch that alongside `ArgumentException`.
  - A process starts under the runner's gate, so a stop either sees it and signals it or is seen first and nothing starts. Keep that when touching `Launch` (restarts made the window real).
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
- **Avalonia styles:** a local `Foreground` beats a class style, so state colours (`.up`, `.bad`) must come from styles.
- **`grep -r` + `sed -i` across `src/` hits `bin/` binaries.** Exclude build output.
- **Stopping processes by hand:** the harness blocks `taskkill /PID …`. Use `Stop-Process` and walk children with `Get-CimInstance Win32_Process`.
- **The SDK's `Sprig.Text` value type shadows `Spectre.Console.Text`** inside `Sprig.*` namespaces; the CLI aliases it as `SpectreText`.
- **A referenced exe project** copies a bare `.exe` stub into referencing projects; the host locators only accept an exe with its `.dll` beside it.
- File-based apps default to Native AOT settings, so the SDK uses source-generated JSON (`ModelJsonContext`, polymorphic on `kind`).
- Roslyn needs the `FileBasedProgram` parse feature or every `#:` line is an error.
- Front-end packages are pinned to releases at least two weeks old. Install with `npm ci --ignore-scripts`.

## Loose ends (known, not yet done)

- **Flaky host tests under full-suite load (cause unknown).** Roughly one full run in three fails one `HostTests` test, on this branch's last two commits alike: either an app "didn't stop within 10s of Ctrl+C" (usually `A_runner_logs_its_output...`), or a fresh host stops answering for 20 s (`A_failed_build...` times out). Each test passes on its own, every time. `sprig-host` doesn't crash (the Application event log, id 1026, shows host crashes; none since the `ProcessName` fix). It was *not* `cmd` swallowing a Ctrl+C before starting `ping`: it failed with `ping` confirmed running. Suspects: the host stalling under load (thread pool, or endpoint scanning of new processes), or Ctrl+C delivery with several hidden consoles about. The failing stop test now puts the runner's log in its message, so the next failure says what was still running.
- **EDR:** nothing was blocked on this machine, but whether CrowdStrike or Defender raised non-blocking alerts for the host (process trees, console attach, Ctrl+C) is a question for SecOps. Worth asking before going much further.
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
