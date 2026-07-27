# Sprig — Guided Tour Implementation Plan

A milestone-based plan for an **interactive walkthrough** that shows a new user what a *working*
sprig setup looks like, by handing them one — pre-built, fully populated, and safe to break.

> **Status: M1–M5 shipped.** Full suite green (432 tests, up from 393), engine behaviour unchanged,
> verified via headless render (`sprig-gui render <dir>` → `tour_stop1..5`, `tour_stop_infra`,
> `tour_building`, and the per-page `tour_*` frames) — see `captures/20260727-guided-tour-m*`.
> Three departures from the plan as written are recorded in §10.

The problem this solves: sprig's learning curve is front-loaded. A first-run user faces three
empty tables and a vocabulary (repo / stack / workspace / input / binding / port) that only makes
sense once you've seen values flowing through it. The existing front door
([`frontdoor-plan.md`](./frontdoor-plan.md)) tells them *what to do next*; it cannot show them
*what "done" looks like*, because on a fresh install there is nothing to show.

---

## 0. Framing & guiding principles

- **Show, don't narrate.** The deliverable is a real, populated, clickable setup — not a slideshow
  and not arrows pointing at empty tables.
- **One isolation mechanism, reused.** The tour runs against a **separate central store**
  (`sprig (Demo)`), exactly as a Debug build already runs against `sprig (Dev)`
  (`src/Sprig.Core/Store/AppProfile.cs`). The user's real repos and real store are never touched.
- **The real engine, or it's worthless.** The tour calls `RepoRegistryStore.Add` → `StackStore.Save`
  → `StackResolver.Resolve` → `WorkspaceService.Create`. It never fabricates store JSON. If the
  sample can't be built by the real code path, that's a bug the user would have hit too.
- **Exactly one branch in the codebase.** The only thing that knows about the tour is the store
  root handed to `AppServices`, plus a banner. If `if (IsDemo)` ever appears inside
  `ReposViewModel` / `StacksViewModel` / `WorkspacesViewModel`, the design has failed — see
  §7, *The maintenance contract*.
- **`Sprig.Core` gains a seeder, nothing else.** No schema change, no new persisted concepts, no
  changes to resolution, allocation, or generation.
- **Docker is optional.** The tour's payoff is *"here is the compose file sprig generated for
  you"* — real, instant, and offline. Starting containers is an explicit extra step (§6).
- **Disposable by construction.** Everything the tour creates lives under one directory. Teardown
  is "stop what's running, then delete that directory".

---

## 1. The design decision (and what was rejected)

### Chosen: a sandboxed sample setup

Point the app at `%LOCALAPPDATA%\sprig (Demo)`, scaffold two throwaway git repos inside it, wire
them into a stack, create a workspace from that stack. Every page now renders populated with real
data, and a narration strip walks the user through what they're looking at.

### Rejected: coach marks / spotlight overlays

The familiar "highlight a control, show a tooltip, click Next" pattern fails here on both counts:

- **It can't show a working setup.** On a fresh install the pages it would highlight are empty. The
  user learns where the buttons are, not what the model does — which is the actual gap.
- **It's the most brittle thing you could maintain.** Each step anchors to a named control in a
  specific view. Renaming a button, re-nesting a `Border`, or reordering a panel breaks a step
  *silently* — no compile error, no test failure, just a tooltip pointing at nothing.

### Rejected: fabricated store records

Writing `repos.json` / `stacks/*.json` / `instance.json` by hand without real repos on disk is
cheaper for about a day. Then: `WorktreeInspector` and `WorkspaceReconciler` shell out to git,
find no worktree, and report drift — so the "working setup" demo renders as a broken one. Worse, it
would need fake-aware paths through the ViewModels, which is precisely the tax §7 exists to avoid.

---

## 2. Where it plugs in

| Area | File(s) today | Change |
|---|---|---|
| Demo store root | `Store/AppProfile.cs` (`DataFolderName`) | Add `DemoFolderName` (one expression). |
| Composition root | `App.axaml.cs:20-21` (`new AppServices()` → `MainWindowViewModel`) | Allow the window's DataContext to be rebuilt against a different store root. **The only branch.** |
| **New** seeder | — | `Sprig.Core/Demo/SampleSetup.cs` — scaffolds repos, then drives the real stores. |
| **New** sample fixtures | — | Two `.sprig.json` files + a compose file + an env template, embedded as resources (same pattern as `CHANGELOG.md` in `Sprig.App.csproj:24-27`). |
| Narration strip | `ViewModels/SetupGuideViewModel.cs`, `Views/MainWindow.axaml:163-183` | Add a *tour* mode alongside the existing *setup* mode; reuse the strip chrome verbatim. |
| Tour entry point | `Views/HomeView.axaml:93` ("Walk me through setup"), `HomeViewModel.cs:85` (`StartGuide`) | Add a sibling action: "Show me a working setup". |
| Exit + teardown | `ViewModels/SettingsViewModel.cs` | "Leave the tour" / "Delete the sample" — plus the banner's own exit affordance. |
| Demo banner | `Views/MainWindow.axaml:154-162` (update-notice bar) | Reuse the top-bar pattern; amber, persistent, with "Exit tour". |

The tour introduces **no new data source**. It introduces a second *instance* of the existing one.

---

## 3. The sample fixture

Two repos, because one repo cannot demonstrate the thing that actually confuses people: a value
produced by the stack and consumed by *two* repos at once.

```
<demoRoot>\
  sample\
    sample-api\          git repo, one seed commit, .sprig.json, docker-compose.yml, .env.template
    sample-web\          git repo, one seed commit, .sprig.json, .env.template
    sample-api--tour\    ← worktree, created by the real engine
    sample-web--tour\    ← worktree, created by the real engine
  repos.json  stacks\  instances\  ports.json  settings.json
```

Worktrees land **inside** the demo root for free: `WorkspaceService.Create` places each worktree as
a sibling of the repo root (`WorkspaceService.cs:124-131`), and the repo roots are already inside
it. So deleting `<demoRoot>` removes the store, the sample repos, and every worktree in one call.

**`sample-api/.sprig.json`** — declares what it consumes, and overrides its compose port:

```json
{
  "schema": 2,
  "name": "sample-api",
  "inputs": [
    { "name": "port", "example": "5000", "description": "Port the API listens on" },
    { "name": "dbPort", "example": "5432", "description": "Port the sample database binds to" }
  ],
  "env": [
    { "file": ".env", "templates": [".env.template"],
      "set": { "PORT": "${sprig.port}", "DATABASE_URL": "postgres://localhost:${sprig.dbPort}/app" } }
  ],
  "compose": [
    { "file": "docker-compose.yml",
      "overrides": [ { "path": ["services", "db", "ports", "0"], "template": "${sprig.dbPort}:5432" } ] }
  ]
}
```

**`sample-web/.sprig.json`** — consumes a value *composed from* the API's port, which is the whole
one-directional-flow lesson in one line:

```json
{
  "schema": 2,
  "name": "sample-web",
  "inputs": [
    { "name": "port", "example": "3000" },
    { "name": "apiUrl", "example": "http://localhost:5000", "description": "Where the API is" }
  ],
  "env": [
    { "file": ".env", "templates": [".env.template"],
      "set": { "PORT": "${sprig.port}", "VITE_API_URL": "${sprig.apiUrl}" } }
  ]
}
```

**The sample stack** (built as a `StackDefinition`, saved via `StackStore.Save`):

```
Ports:    api_port, web_port, db_port
Bindings: sample-api  → port   = ${sprig.ports.api_port}
                        dbPort = ${sprig.ports.db_port}
          sample-web  → port   = ${sprig.ports.web_port}
                        apiUrl = http://localhost:${sprig.ports.api_port}
Shares:   api_port → [(sample-api, port), (sample-web, apiUrl)]
```

That `Shares` entry is deliberate: it makes the wiring canvas draw a shared-port cable, so the
tour's most abstract idea arrives as a picture rather than a paragraph.

**`sample-api/docker-compose.yml`** stays deliberately boring — one `postgres:16-alpine` service
with a named volume. It is there to be *overridden and regenerated*, which is a read-only lesson;
whether it ever starts is a separate, optional step.

> **Fixture authoring rule:** these files are *content*, not code. They are stored verbatim as
> embedded resources and written to disk unmodified. Nothing generates them, so nothing can
> generate them wrongly — and §8's test loads these exact bytes through the real validator.

---

## 4. Seeding, as the real engine sees it

`Sprig.Core/Demo/SampleSetup.cs` — a single class, no UI knowledge, `IProgress`-reporting so the
existing progress window can host it:

```csharp
public sealed class SampleSetup(
    ISprigPaths paths, IGitService git, RepoRegistryStore repos,
    StackStore stacks, StackResolver resolver, WorkspaceService workspaces)
{
    public const string Workspace = "tour";

    /// <summary>Scaffold the sample repos and build a complete setup in this (demo) store.
    /// Idempotent: an existing, healthy sample is reused rather than rebuilt.</summary>
    public InstanceRecord Build(IProgress<WorkspaceStepProgress>? progress = null);

    /// <summary>Remove everything the sample owns: containers, worktrees, store, sample repos.</summary>
    public void Destroy();
}
```

`Build` does, in order:

1. **Scaffold** `sample-api` and `sample-web`: create the directory, write the embedded fixture
   files, then `git init -b main` / `add -A` / `commit`. This is exactly what
   `tests/Sprig.Tests/TempGitRepo.cs` already does — lift the approach, including its retry-on-delete
   handling, which exists because Windows holds git pack files briefly.
2. **Register** both via `RepoRegistryStore.Add(path, name)` — the real registry, so the real
   `.sprig.json` load and validation run.
3. **Save** the stack via `StackStore.Save` — the real store, so binding/share validation runs.
4. **Resolve** via `StackResolver.Resolve("sample")`.
5. **Create** via `WorkspaceService.Create(resolved, "tour", progress)` — real worktrees, real port
   allocation from the demo store's own `ports.json`, real env clobbering, real compose generation.

Note what step 5 does *not* do: start containers. `Create` generates compose files and stops
(verified — the app starts infra separately at `WorkspacesViewModel.cs:251`). The tour inherits that
for free.

**Idempotence matters more than it looks.** Users will exit mid-tour, kill the app, or re-enter.
`Build` must detect a usable existing sample and return it, and must be able to recover from a
half-built one — for which `WorkspaceReconciler` already exists. Rule: if `Build` finds a demo root
it can't make sense of, it calls `Destroy` and starts clean. A demo store is worth nothing, so
never ask the user to repair one.

---

## 5. Milestones

Each milestone is independently shippable and verified the way the front door was: ViewModel unit
tests plus headless-render snapshots (`src/Sprig.App/Rendering/HeadlessRenderer.cs`).

### M1 — The sample builds (no UI)

`SampleSetup` + the embedded fixtures + `AppProfile.DemoFolderName`. Driven only by a test and,
optionally, a hidden CLI verb (`sprig demo build` / `sprig demo destroy`) — which is also the
fastest way to eyeball the result during development.

*Exit:* a test builds the sample end-to-end into a temp store and asserts the instance record has
two repos, three allocated ports, two generated compose files, and `.env` files containing the
resolved values. `Destroy` leaves no directory behind. Suite green.

### M2 — Entering and leaving the tour

The store-root swap in `App.axaml.cs`, the amber banner, and the exit path. No narration yet —
entering the tour drops you on a fully populated Home, which is *already most of the value*.

*Exit:* enter from Home, click through Repos / Stacks / Workspaces seeing real sample data, exit,
and confirm the real store is byte-identical to before. Headless snapshots: `demo_home`,
`demo_repos`, `demo_stacks_detail`, `demo_workspaces`.

### M3 — Narration

Extend `SetupGuideViewModel` with a tour mode: an ordered list of steps that *navigate and explain*
rather than launch modals. Roughly five steps, each a sentence and a destination:

1. *Repos* — "Two repos are registered. Each declares only what it needs, never what it provides."
2. *Stacks* — "This stack owns three ports and supplies every value both repos asked for."
   (canvas open, shared cable visible)
3. *Workspaces* — "One workspace: two worktrees, two branches, three ports, nothing colliding."
4. *The generated `.env`* — "Here's the file sprig wrote. These numbers came from the stack."
5. *The generated compose* — "Same story for infra: your compose file, with ports rewritten."

*Exit:* stepping forwards and backwards is stable, each step lands on the right page with the right
panel open, and the strip's existing "skip" closes it without leaving the tour.

### M4 — Optional infra

A single "Start the containers" step, shown only when `IDockerService.IsAvailable()` is true, that
runs the real `Up` and then shows live status. Skipping it must leave steps 1–5 fully meaningful.

*Exit:* with Docker stopped, the tour completes with the step absent and no error surfaced. With
Docker running, containers start under project `sprig-tour` and appear in the detail view.

### M5 — Polish & discoverability

First-run prominence on Home (a new user should not have to hunt for this), a "Show me a working
setup" affordance on each empty state, and a Settings row to delete the sample and reclaim disk.

*Exit:* teardown is reachable from three places and always fully cleans up; the tour is offered
before the user has to guess.

---

## 6. Docker policy

The tour must be genuinely useful with no Docker daemon, no network, and no pulled images —
because that describes a meaningful share of first launches, and because a walkthrough that fails
at step one teaches the wrong lesson about the tool.

- Steps 1–5 need **zero** Docker. Compose *generation* is pure file I/O.
- The container step is gated on `IDockerService.IsAvailable()` and is always skippable.
- The sample's only image is `postgres:16-alpine`, and the tour warns before a first pull.
- Teardown always attempts `Down(removeVolumes: true)` before deleting files, and tolerates every
  failure — a missing daemon must never block cleanup of the directory.

---

## 7. The maintenance contract

This is the section to re-read in six months. Everything above is additive and cheap; these are the
costs that are actually ongoing, and the specific thing that keeps each one from compounding.

### Cost 1 — the fixture tracks the config schema *(the real one)*

Bump `SupportedSchema` to 3, change binding syntax, add a required field, and the sample silently
rots. `SprigConfigLoader` does an **exact** schema match, so a stale fixture doesn't degrade — it
fails outright, on a new user's very first click.

**Mitigation, and the reason this feature pays for itself:** M1's test builds the whole sample
through the real loader, validator, stores, resolver, and `WorkspaceService`. A schema change then
fails CI rather than a first run. That test is also the only one in the suite that exercises
`repo → stack → workspace` as a single continuous path, using the same fixtures a user sees — so the
tour stops being a maintenance liability and becomes the integration test the suite currently lacks.

### Cost 2 — narration copy drifts from the UI

Tour text that names buttons and positions ("click the third tile") goes stale invisibly.

**Mitigation:** copy describes *concepts and values*, never chrome. Where a label must be quoted,
bind it to the same property the button binds to — `SetupState` already does exactly this
(`NextCta` feeds both the Home banner and its button, so they cannot disagree). Follow that
precedent rather than duplicating strings.

### Cost 3 — mode-awareness leaking into the app

The failure mode that would make this permanently expensive: `if (IsDemo)` appearing throughout the
ViewModels, so every future change has to be reasoned about twice.

**Mitigation, stated as a hard rule:** the demo differs from a real run in exactly two ways — the
store root and a banner. No ViewModel below `MainWindowViewModel` may take a dependency on demo
mode. If a feature seems to need one, the right fix is almost always to make the *sample* more
realistic instead. Worth a comment on `DemoFolderName` saying so.

### Cost 4 — leftovers on a user's machine

Worktrees, containers, volumes, and a couple of git repos.

**Mitigation:** the containment property from §3 — one directory holds everything — plus a
teardown that tolerates partial failure, plus `Destroy`-then-rebuild rather than repair. Ship M5's
Settings row so a user who abandoned the tour can always reclaim the space.

### What this does *not* cost

- No engine change, so no risk to real workspaces.
- No schema change, no migration, no new persisted concept.
- No new UI framework work: the banner, the strip, the progress window, and the wiring canvas all
  already exist and are reused as-is.

---

## 8. Testing

| What | How |
|---|---|
| Sample builds end-to-end | `SampleSetup.Build` against `TempStore` + a real `ProcessRunner` for git; assert record, ports, `.env` contents, generated compose. |
| Fixtures are valid | Load each embedded `.sprig.json` through `SprigConfigValidator` — catches a schema bump immediately. |
| Stack fixture is valid | `StackStore.Save` on the sample definition — catches binding/share rule changes. |
| Teardown is complete | `Destroy` then assert the demo root is gone; run it on a half-built sample too. |
| Idempotence | `Build` twice → one workspace, no duplicate ports, no exception. |
| No Docker required | Build + narrate with `FakeDockerService` reporting unavailable (`tests/Sprig.Tests/FakeDockerService.cs`). |
| Tour VM | Step order, forwards/backwards, and skip — plain VM tests, as `App/SetupStateTests.cs` does. |
| Visual | Headless snapshots per M2/M3. |

---

## 9. Open questions

1. **Naming.** *Resolved:* "guided tour" in the UI, `Demo` in the code (`DemoFolderName`,
   `IsDemoStore`) since that names the *store*, not the experience. A Debug build's demo store is
   `sprig (Dev) (Demo)` — clunky but honest, and it only ever appears in a path.
2. **Should the CLI expose it?** *Still open.* `sprig demo build/destroy` is nearly free now that
   `SampleSetup` exists, and would be useful for development as much as for CLI-first users.
3. **Graduation path.** *Resolved:* the tour's last stop is the handoff — it says what a repo needs
   (one committed `.sprig.json`, which sprig writes for you) and that leaving deletes the sample.
   It does not auto-launch the setup guide; landing the user on a real, empty Home with the model
   fresh is enough, and forcing them into another flow on the way out would be presumptuous.
4. **Should the sample include a deliberate failure?** *Still open, still tempting.* `doctor` /
   reconcile is a genuine selling point and can't be shown on a healthy setup. The cheap version:
   a sixth stop that deletes a worktree behind the user's back and then repairs it.

---

## 10. Departures from this plan during implementation

Recorded because each was a decision made against the plan, not an oversight.

1. **Stops 4–5 were re-planned (§5, M3).** The plan assumed an in-app viewer for the *generated*
   `.env` and compose file. There isn't one — `EnvOverlay`/`ComposeOverlay` are for *authoring*
   declarations, and generated files are reached via "Open in…". Rather than build a viewer (real
   scope, and the plan's whole premise is reusing what exists), stop 4 narrates the resolved values
   already on the Workspaces detail panel and points at the worktree, and stop 5 became the
   graduation step that was parked as open question 3.
2. **Narration is its own view model, not a mode on `SetupGuideViewModel` (§2, M3).** The plan said
   extend it. On reading it closely, that class is a projection of *store counts* that advances when
   the user creates something; the tour is a fixed script advanced by an index over a setup that
   already exists. Sharing one class would have meant every property meaning two things depending on
   a mode flag — the exact cost §7 exists to avoid. They share the strip's styles instead, which is
   where restyling actually happens.
3. **`IsDemoStore` is declared, not inferred (§2, M2).** The first cut compared the store root
   against `SprigPaths.DemoRoot`. Making the caller state it is both more honest and what lets the
   headless renderer stand up a real tour session in a temp directory — which is how every
   `tour_*.png` frame gets produced.

Also worth noting, since it was a bug rather than a decision: `Destroy` originally failed on
Windows, because git writes its object files read-only and `Directory.Delete` refuses those. No
amount of retrying fixes it — the attribute has to be cleared first. That affects any code deleting
a git repo on Windows, and `TempGitRepo` in the tests has the same latent problem (it swallows the
failure, so it leaks temp directories instead of reporting).
