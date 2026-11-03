# Handover: experimental branch

Written 2026-11-03 at the end of the session that did M0–M3. Read this first, then [experimental-roadmap.md](experimental-roadmap.md).

## Where things stand

- Branch `experimental`, latest commit `d9c549d`. Nothing pushed. `main` is untouched.
- M0, M1, M2 and M3 are done. **M4 is next** and waits on two decisions (below).
- 112 tests pass (`dotnet test sprig.slnx`, about 40 s; the integration tests compile real definitions with `dotnet run`).
- Warnings are errors repo-wide (`Directory.Build.props`). Build with normal verbosity: `-v q` hides warnings.
- Version is `0.100.0-exp`. **Never tag from this branch**: the release workflow publishes on any `v*` tag. Don't use the `bump-version` or `release-next-version` skills here.

## Decisions already made (don't reopen)

| Topic | Decision |
|---|---|
| Authoring | C# file-based apps. Each repo has a `sprig.cs` (`Repo.Describe`); a trellis file `<name>.trellis.cs` (`Trellis.Describe`) joins repos. |
| Names | **Trellis** (the file joining repos) and **sprout** (one running copy). Not "system", "composition", "map" or "workspace". |
| Trellis location | Any folder, registered once with `sprig trellis add <file>`. |
| Editing | External IDE first (IntelliSense confirmed in VS Code/Rider via `#:package Sprig.Sdk`). The in-app editor works but needs style polish (in Later). |
| Infra | Code presets (`Postgres`, `Redis`, `RabbitMq`, `Mailpit`), and a repo's own compose file is allowed (`repo.ComposeFile(...).Service(...).Endpoint(...)`, `.Set(path, value)`). |
| Run experience | Log pane per runner in the app (M5). Telemetry later. |
| Store | `%LOCALAPPDATA%\sprig-exp` (`sprig-exp (Dev)` for Debug). Never the released app's `sprig` folder; a test guards it. |

## Decisions M4 needs from the user

Ask these before writing M4 code. My recommendation is in each.

1. **How .NET apps get their values when run by hand.** .NET reads process env vars, which only runners (M5) can set. Recommendation: sprig writes a `Properties/launchSettings.json` profile in each worktree (used by `dotnet run` and IDEs). Node apps already get `.env.local` via `EnvFile`.
2. **Compose services a repo's `sprig.cs` doesn't name.** They keep the file's fixed ports and would clash between sprouts. Recommendation: `sprig plan` warns about them, and the generated sprout compose leaves them out unless named. (The testbed's Mailpit is behind a `standalone` profile for this reason.)

## How the pieces fit

```
repo/sprig.cs ──┐                         dotnet run, scrubbed env, time limit,
repo/sprig.cs ──┼─ DefinitionRunner ──►  trusted by SHA-256 of content, cached
x.trellis.cs ───┘   (Engine/Definitions)   → DefinitionModel JSON (RepoModel | TrellisModel)
                         │
                  TrellisLoader (Engine/Trellises) — evaluates trellis + repos in parallel
                         │
                  Planner (Engine/Planning) — scopes per repo, binds imports→exports or
                         │                    trellis bindings, allocates preview ports,
                         ▼                    resolves {templates} with a "via" trail
                       Plan  →  `sprig plan` (CLI)  /  Trellises page (App)
```

- **Projects:** `Sprig.Sdk` (what definitions reference; the model in `Model.cs` is the contract), `Sprig.Engine`, `Sprig.LanguageHost` (Roslyn for the in-app editor, separate process), `Sprig.App` (Avalonia 12), `Sprig.Cli` (Spectre), `tests/Sprig.Tests`.
- **Templates:** values are `Expr` placeholders. `$"...{api.Port}..."` goes through `ExprHandler`; the emitted form is text with `{path}` and doubled braces for literals. Paths: `res.endpoint.port|url`, `import.<name>`, `export.<repoPath>.<name>`, `sprout.name`.
- **Scoping:** a repo's names are private to it; crossing repos is only by import/export or a trellis `Bind`.
- **Ports in a plan are a preview.** Nothing is leased yet; M4 adds persistent leases (stable policy) and fresh ones (random policy).
- **Safety model:** `DirectivePolicy` allows only `#:package Sprig.Sdk@…` (plus `#:project …/Sprig.Sdk.csproj` in Debug builds). Trust is per path + content hash (`trust.json`). It's not a sandbox: an approved definition runs with the user's rights.

## Testbed

- Templates in `testbed/repos/{shop-services,shop-web,shop-mobile}` plus `testbed/storefront.trellis.cs`.
- `pwsh -File scripts/testbed.ps1 [-Path <dir>] [-Force] [-UsePackage]` builds real git repos (default `../sprig-testbed`). It rewrites `#:package Sprig.Sdk` to a `#:project` reference to this checkout unless `-UsePackage`. `-Force` only deletes a folder holding its `.sprig-testbed` marker.
- Try it: `sprig trellis add <testbed>/storefront.trellis.cs --trust`, then `sprig plan storefront`. Use a throwaway store with `SPRIG_STORE=<dir>`.
- Each app has a config page (`/config` on the APIs, a Config tab on the front ends) showing each value, its source and whether its target answers. That's how M4/M5 should prove wiring.

## Gotchas found the hard way

- **Windows 8.3 short paths:** `%TEMP%` here is `C:\Users\JONHOW~1\…`. Vite returns 403 from such a path. Always pass paths through `Engine/Store/LongPath.Of`. Use `C:\Users\JonHowell\AppData\Local\Temp\…` for temp testbeds.
- **This machine already runs containers** on 5432 and on 8004–8006, 8012–8014 and others. Never stop or remove containers you didn't start. The allocator skips listening ports; testbed compose host ports can be moved with `PG_PORT` etc.
- **`grep -r` + `sed -i` across `src/` hits `bin/` binaries.** Exclude build output, or delete `bin`/`obj` and rebuild afterwards.
- **Stopping processes:** the harness blocks `taskkill /PID …` (it misreads `/PID` as a path). Use `Stop-Process` and walk children with `Get-CimInstance Win32_Process`.
- **The SDK's `Sprig.Text` value type shadows `Spectre.Console.Text`** inside `Sprig.*` namespaces; the CLI aliases it as `SpectreText`.
- **A referenced exe project** copies a bare `.exe` stub into referencing projects; `LanguageHostClient` only accepts a host with its `.dll` beside it.
- File-based apps default to Native AOT settings, so the SDK uses source-generated JSON (`ModelJsonContext`, polymorphic on `kind`).
- Roslyn needs the `FileBasedProgram` parse feature or every `#:` line is an error.
- Front-end packages are pinned to releases at least two weeks old. Install with `npm ci --ignore-scripts`.

## User conventions to keep

- Path fields use `Controls/PathBox` (folder button, Tab completion, forward slashes, including when displaying stored paths).
- Ask before taking screenshots of non-desktop UI. Desktop captures go in `captures/{timestamp}-{reason}.png` and are never deleted. Headless renders: `sprig-gui render-editor <png>` and `sprig-gui render-trellises <png> <trellis.cs>`.
- No private repo or project names in docs, artefacts or examples. Use the testbed names (shop, catalog, orders).
- Code comments explain why, never what the code used to do. Markdown has no hard-wrapped sentences.
- Commit messages end with the `Co-Authored-By` line. Commit when asked; the user said to commit milestone work as it lands.

## M4 in short (see the roadmap for the full list)

`sprig up storefront --sprout feat-x --branch feat-x` in one step: worktrees (or in place), leased ports, env files (and the .NET answer from decision 1), a generated compose project per sprout (`docker compose up -d`, wait for health), plus `sprig down` and `sprig rm`. App: "New sprout" flow, sprouts list, and the progress window (bring `OperationProgressWindow` back from `main`, adapted). Done when two sprouts of `storefront` run side by side with separate ports and Postgres volumes, and the monorepo also runs in place.

Helpers worth copying back from `main` (`git show main:<path>`): `src/Sprig.Core/Git/GitService.cs` (worktrees), `src/Sprig.Core/Ports/FilePortStore.cs` (leases), `src/Sprig.Core/Compose/ComposeGenerator.cs`, `src/Sprig.Core/Env/EnvClobberService.cs`, `src/Sprig.App/Views/OperationProgressWindow.axaml`. Adapt them; don't restore the old model.
