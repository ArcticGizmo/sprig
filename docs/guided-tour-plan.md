# Sprig — Guided Tour Implementation Plan

A milestone-based plan for an **interactive walkthrough** that shows a new user what a *working*
sprig setup looks like, by handing them one — pre-built, fully populated, and safe to break.

> **Status: M1–M5 shipped; M6 (coachmarks) spiked, triaged, and the first-run fixes done — see §11;
> M7 (guide library) shipped, four guides — see §12, §14, §15, §16; the tour is coachmarks too — see §13.**
> Full suite green (457 tests, up from 393), engine behaviour unchanged, verified via headless render
> (`sprig-gui render <dir>` → `tour_stop*` with spotlight, `coach_case*`, `guide1_*`, `row_highlight`)
> — see `captures/20260727-*`. Three departures from the plan as written are recorded in §10.

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

---

## 11. M6 — coachmarks: mechanism spike and triage

The sandbox answers *"what does a finished setup look like?"*. It does not answer *"what do I type in
these fields?"* — the wall of inputs a first-timer meets on **add repo → repo config → stack builder**.
Coachmarks answer that, and coaching *inside the sample* means a beginner can type into real fields
with nothing at stake.

### 11.1 The mechanism is proven

Spiked and shipped ahead of any script (`src/Sprig.App/Coach/`), against the three anchor cases a real
script has to survive — verified in `captures/20260727-coach-spike-final/coach_case{1,2,3}.png`:

| Case | How it anchors | Result |
|---|---|---|
| Plain control | `AutomationProperties.AutomationId` in XAML | One attribute, no coach code in the view |
| Below the fold | `BringIntoView()` then flush layout before measuring | Correct rect; callout flips side and clamps on screen |
| Drawn, not a control | `IAnchorSource` on `WiringCanvas` | Exact; resolves two nested `ScrollViewer`s deep |

`AutomationProperties.AutomationId` rather than a bespoke attached property, because it costs nothing
when the coach isn't running and earns its keep twice more — accessibility identity, and a hook for any
future UI test. The canvas publishes the rects `BuildLayout` already computes for hit-testing, keyed on
domain identity, so a highlight can never disagree with what the user can click.

**Brittleness — the objection that normally sinks coachmarks — is handled by two checks.** Source-scanning
tests keep `Anchors.Chrome` and the views in step in *both* directions, and the headless renderer *fails*
(exit 1) when a mark's anchor doesn't resolve, which catches "declared in XAML but never realised".
Verified by renaming an anchor and watching three tests fail with actionable messages.

Anchoring turned out to be the easy half. The expensive parts are **preconditions** (every mark needs the
app put into a state where its target exists — that belongs in `Navigator`, e.g. `OpenStackBuilderWired`)
and **callout placement on the canvas**, where a 380px callout covers a lot of board.

### 11.2 Triage: fix in place, or coach?

The premise of this pass: **if a screen needs a coachmark to be usable, the coachmark becomes its
permanent documentation.** So every element on the first-run path was classified as *fix* (an in-place
change removes the need to explain), *coach* (a real concept, worth teaching once), or *leave* (already
clear). The important correction it produced: the wall is **not** fourteen blank fields. `InitInspector`
already proposes a complete config from detected env keys and compose ports, so the user is *reviewing*
pre-filled fields, not authoring. That is a comprehension problem, not a data-entry one — which is why
the count below is 6–8 marks rather than the 20–25 the field count implies. You teach concepts once.

#### Add a repo — not the wall

| Element | Verdict | Why |
|---|---|---|
| Folder + Browse + live git detection + "found a .sprig.json / it'll be created" | **Leave** | One decision, live feedback, states the consequence. Genuinely good. |

#### Repo config editor — the first real wall

| Element | Verdict | Why |
|---|---|---|
| **Scaffold notes are discarded** | **Fix — highest value on the path** | `ReposViewModel.cs:300` calls `Init.Inspect` and drops `proposal.Notes`; the CLI prints them (`CliApp.cs:212`). sprig already explains what it guessed and why, then bins it in the GUI. Surfacing them answers "why are these fields here and where did these values come from?" — and removes an estimated 3–4 marks. |
| "INPUTS (supplied by the stack)" | **Coach** (once) | "Input" is *the* load-bearing concept. Not a UI defect — teach it. |
| **"Example" column** | **Fix** | The label implies a default, or a value that gets used. It is documentation for whoever writes the binding. Rename, or explain in the header. |
| **"Allowed ports" column** | **Fix — progressive disclosure** | Auth0-class advanced feature at equal visual weight to Name; almost every repo leaves it blank. Collapse behind a per-row "restrict…". Removes a mark *and* declutters permanently. |
| "Gitignored — safe to override", "Found in the repo" | **Leave** | Live, specific, already teaching. |
| Merged-view explanation sentence | **Leave** | Already does the job. |
| **"REPLACEMENTS" panel** | **Fix** | A second representation of the same data as the merged view directly above it. Two views of one thing is a comprehension cost, not a feature. |
| Compose overrides — YAML path syntax (`services.db.ports.0`) | **Coach** | Path-based targeting is a genuine concept. |

#### Stack builder — the second wall

| Element | Verdict | Why |
|---|---|---|
| **Auto-wire is opt-in** | **Fix — highest value on this surface** | After auto-wire the builder is nearly done. Opening it with repos selected should wire immediately and frame the canvas as *"review what I guessed"*. That turns authoring into reviewing and removes most of the need to coach the drag gesture at all. |
| **The instruction paragraph** | **Fix, then coach the gesture once** | Five distinct interactions in one run-on sentence, and visually truncated at the panel edge ("click a f node"). Prose compensating for undiscoverable affordances. Fix the truncation and split it per element; coach drag-to-wire once, on a real port. |
| **Shared ports** (the `api-port_2` case) | **Coach** | `StackAutowire` deliberately never assumes sharing — two services' own listening ports must not collide (`StackAutowire.cs:26-29`). Intent about sharing is the one thing it cannot infer, so this is a real teaching moment, and it is exactly the concept the sample stack was built around. |
| TRANSFORM column header with no transform present | **Leave** (or hide until one exists) | Cosmetic. |
| Port rename/remove, transform editing, line selection | **Leave to discovery** | Secondary; coaching all of it is how a 6-mark script becomes 25. |

### 11.3 What shipped, and the one fix that was withdrawn

Four of the five fixes shipped as triaged. The fifth — auto-wiring the canvas on open — was
**implemented, found to be unsafe, and reverted**, which is worth recording because the idea is
obviously appealing and will come up again.

**Shipped:**

- **The scaffold explanation.** `ReposViewModel` keeps `InitProposal.Notes` and the editor opens with a
  panel: *"sprig filled this in for you"*, listing what it detected. Dismissable.
- **"Example" → "Example shape"**, with a tooltip stating it is documentation only and never used as a
  value.
- **Port restrictions behind a `restrict…` link** per row, which reveals itself automatically whenever a
  restriction already exists — so an existing one is never hidden from its owner.
- **"REPLACEMENTS" → "KEYS SPRIG WILL REWRITE"**, plus a line saying every other line is copied through.
  The panel was *relabelled rather than removed*: it looked like a duplicate of the merged view above it,
  but it is the only place to remove an individual override, so deleting it would have cost a feature to
  satisfy a triage note.
- **The builder's instruction paragraph** now leads with Auto-wire as the one action that does the work,
  with the five gestures following as reference rather than as the first thing read.

**Withdrawn, then shipped properly — auto-wire on open.** The first attempt was unsafe.
`StackAutowire` reuses an existing port whose name matches (`StackAutowire.cs:93`). That is right when the
port was named by the *user*, but wiring incrementally as each repo is selected feeds it ports auto-wire
itself invented moments earlier, so the second repo adopts the first repo's port. Two services that each
declare `port` would be silently pointed at one and collide at runtime — precisely the hazard the batch path
documents itself as avoiding (`StackAutowire.cs:26-29`), reintroduced through the back door. It was visible
in the render as `api-port SHARED ×2` where batch auto-wire produces `api-port` and `api-port_2`.

The fix was **port provenance**, now implemented. `StackPortRow.Auto` and `BindingRow.Auto` record whether
auto-wire or the user produced each row, and `AutoWire` begins by discarding its own previous proposal
(`DiscardAutoWiring`) so every run is a fresh batch pass over the user's state alone — never one fed the
ports it invented a moment ago. Ownership transfers to the user on any act of intent: adding a port, naming
one, typing an expression, or wiring from the canvas. `SetPorts` carries provenance across the rebuild, so a
port the user named doesn't quietly become auto-wire's to delete.

With that in place the canvas wires itself as repos are selected, and correctly: distinct ports, and sharing
still only ever happens because the user asked for it. Auto-wire is also now idempotent by construction.

Three smaller things the attempt turned up, all kept:

- Editing an existing stack was one step from silently gaining wiring: `EditSelected` selects the repos
  *before* applying the stored bindings, so anything wiring on selection would invent a port for an input
  the saved stack left unbound and keep it. Now covered by a test.
- Several existing tests encoded "the builder starts blank" only incidentally, via a shared helper — setup
  assumptions rather than product assertions. They now start from a deliberately cleared canvas
  (`SelectReposUnwired`), which says what they mean.
- The tests are what caught both problems, twice. Worth trusting that signal on this surface.

### 11.4 What's left

The fixes are done, so the script can now be authored against the improved UI rather than apologising for
the old one. Roughly 6–8 marks: *input* (once), compose path syntax, drag-to-wire, shared ports, and the
handoff. Inside the demo store, so the copy can name `sample-api` and the user can type freely.

One mark's job changed as a result of §11.3: with the scaffold explanation now on screen, the "where did
these values come from?" mark is redundant — that mark becomes "what an input *is*", which the panel
states but does not teach.

Nothing outstanding from §11.3 — port provenance shipped, so the builder now opens pre-wired. The
drag-to-wire mark is consequently less about *how to wire* and more about *how to change* a guess.

Still open: whether coached marks should advance on *the user doing the thing* rather than pressing Next.
More engaging, considerably more machinery (per-mark completion predicates), and it can trap someone who
can't work out the gesture. Recommend Next-to-advance first, and revisit only if the flow feels passive.

---

## 12. M7 — the guide library (vertical slice)

§11 built a coach that *narrates* a finished setup. That teaches the model but never hand-holds a user
through *doing* a task, which is what "so they know where to look" actually asks for. M7 is the layer that
does: a library of small lessons, each teaching one concept by walking the user through performing it in the
throwaway sandbox. This is the vertical slice — the machinery plus guide 1 end to end — deliberately scoped
so the risky parts are proven before four more guides' worth of copy is written.

### 12.1 What a guide is

A **guide** is one concept + the sandbox stage it starts from + an ordered list of coachmarks. Two kinds of
step, and the difference is the whole point:

- **Explanation** — a Next button, for something already on screen.
- **Waiting** (`Completed` predicate + `ShowMe`) — the callout highlights a control and the guide *waits*
  for the user to actually do the thing, advancing itself when the store change it's watching for arrives.
  Nobody is ever trapped: **Show me** performs the action for them, and because it drives the same store
  mutation, the auto-advance can't diverge from the user's own route.

Advance-on-action was the deferred question from §11.4; the user's brief ("hand-hold through the entire
experience") turns it from optional to required. It's the mechanism that makes a guide feel like guidance
rather than a slideshow.

### 12.2 Staged fixtures — the sandbox before the lesson

A guide about *registering* a repo has to start with a repo on disk that *isn't registered yet* — the
opposite of the tour's fully-built end state. So `SampleSetup` gained `BuildTo(SampleStage)` over four
rungs — `RepoOnDisk → ReposRegistered → StackWired → Running` — each the one before it plus one step. A
guide names the stage just *before* the concept it teaches, and entering it rebuilds the sandbox from clean
to that stage. Rebuilding (rather than reusing) is what keeps guides independent and replayable: a
half-finished previous attempt can never leak into the next.

### 12.3 The ladder

Only guide 1 is authored (the slice). The rest are the plan, not code yet:

| # | Guide | Starts at | Teaches |
|---|---|---|---|
| 1 | **Register your first repo** ✅ | `RepoOnDisk` | registration; what an input *is* |
| 2 | Wire a one-repo stack | `ReposRegistered` | ports, bindings — the simplest wiring |
| 3 | Create and run a workspace | `StackWired` | worktrees, port allocation, up/down |
| 4 | Two repos that talk to each other | `ReposRegistered` | the polyrepo lesson: shared ports, the `apiUrl` case |
| 5 | When something drifts | `Running` | breaks a worktree, then repairs it — `doctor`/reconcile, impossible to show on a healthy setup |

A **Learn** nav entry lists them with a duration and a completion tick; progress is recorded in
`SprigSettings.CompletedGuides` against the **real** store (the demo store a guide runs in is deleted on
exit, so completion can't live there).

### 12.4 Guide 1, verified end to end

`captures/20260727-guide1-v2/guide1_*`: the Learn list → the waiting step highlighting **Add repo** with the
modal pre-primed to the sample folder (manual route is a single Confirm) → **Show me** registering the repo,
which auto-advances into the editor → the callout on the repo's declared inputs → the Learn list again with
its tick and a **Replay** button. The headless renderer drives this exactly as a user would and fails the
render if any step's anchor doesn't resolve.

### 12.5 Decisions and cost

- **A guide reuses the tour's store swap, banner, and exit.** A guide runs in the demo store, so the sandbox
  banner and its exit are already there. The tour *narration* strip is now started explicitly by the tour
  path only (not from the view-model constructor), so a guide doesn't also show the tour script. The banner
  copy was made stage-neutral, because "a stack and a running workspace" is false at `RepoOnDisk`.
- **Steps close over `nav`/`services` at build time**, so the runner just invokes `Prepare` / `Completed` /
  `ShowMe` and never threads app state through the walk. Wait predicates are store-shaped (`repos.Get(...)`),
  and `StoreChanged` is treated as UI-thread-raised — the same assumption `HomeViewModel` already relies on,
  which also keeps the machinery unit-testable with no Avalonia dispatcher.
- **The honest cost is the remaining four guides.** The machinery is done and proven; guides 2–5 are
  authored copy plus a completion predicate each. Guide 5 (drift) needs a "break a worktree" sandbox action,
  the one rung that isn't just fixture staging.
- **Escape hatch is "Show me does it for you"**, chosen over skip-the-step (which can leave the sandbox in a
  state a later step assumes) — so the guide is never a dead end.

Still open: whether a waiting step should also accept a UI-only transition (e.g. "the modal is open") as its
trigger, not just a store change. Guide 1 sidesteps it by priming the modal so registration is one store
mutation; a guide that needs to wait on pure UI state would want a lightweight UI signal or a short poll.

---

## 13. The tour becomes coachmarks (spotlight everywhere)

The tour and the guides had grown two different looks: the tour was a narration strip pinned to the top of
the window (`TourGuideViewModel`) that pointed *vaguely* at a page, while the guides used the coachmark
overlay — a floating callout that dims the page and rings its exact target. The prominent entry point ("Show
me a working setup") was the one *without* the eye-direction, which is backwards. So the tour was rebuilt as
a coachmark script, and the strip retired.

**What changed**

- **`TourScript`** replaces `TourGuideViewModel`: the same five/six stops, same copy, now `CoachMark`s. Each
  middle step spotlights a detail panel — `repo.detail`, `stack.detail`, `workspace.detail`, and (Docker-up
  only) `workspace.docker`. The opening and closing beats are **whole-page** steps.
- **Whole-page marks.** `CoachMark.Anchor` is now nullable: null means "dim everything, centre the callout,
  no warning" — an intentional overview beat, distinct from an anchor that was *supposed* to resolve and
  didn't (still a flagged failure). Used for "this is one working sprig" and the "now do it with your repo"
  handoff.
- **Action-on-Next.** `CoachMark.Perform` runs when the user presses Next, before advancing — the tour's
  optional "start the containers" step, carried over verbatim. Distinct from a waiting step's `ShowMe`.
- **One walkthrough engine.** Both the tour and the guides now run through `CoachViewModel` + `CoachOverlay`,
  so there is a single overlay, a single set of anchors, and one visual language for every step. The old
  top strip and its view-model are gone.
- **Docker gate preserved.** The infra step is only in the script when a daemon is up; `MainWindowViewModel`
  probes off the UI thread before starting the tour, exactly as before.

**Anchors added:** `repo.detail` (Repos read-only config panel), `stack.detail`, `workspace.detail`,
`workspace.docker`. The row-level `repo.row:<name>` anchor from the previous commit means a step can also
spotlight one specific repo among several when a guide wants that.

**Verified:** `captures/20260727-tour-spotlight/tour_stop1..5` + `tour_stop_infra`. Step 1 dims the page with
a centred callout; step 2 rings the `sample-api` config panel; the infra step rings just the Docker row. The
render fails if any anchored step doesn't resolve, the same gate the guides use.

Net: the tour a first-time user actually sees now directs the eye to exactly what each sentence is about,
instead of narrating from a strip over an undimmed page.

---

## 14. Guide 2 — wire up a multi-repo stack

The second authored guide, and the first that teaches *building* rather than registering. Starts at the
`ReposRegistered` stage (both sample repos known, no stack) and walks: why a stack → open the builder → read
the auto-wiring → create it → where to go next.

**Shape.** Five steps. Only the last-but-one waits on the user (the store gains a stack — `Create`s
`NotifyStoreChanged` drives the advance); the builder-driving steps are explanation steps whose `Prepare`
opens and wires the builder, because *opening a builder is UI state, not a store change*. This is the
pragmatic answer to the open question from §12.5 — rather than add UI-state polling, a guide drives the UI
transitions itself and only *waits* on the real commits.

**Honest about sharing.** The guide teaches what auto-wire actually produces: each repo's inputs get their
*own* ports, so two services never collide by accident (`StackAutowire` never assumes sharing). The
shared-port case — the web app pointed at the API's exact port — is described as the deliberate drag it is,
not faked. A future guide can teach sharing hands-on; this one teaches composition.

**Two mechanism fixes it forced, both improvements across all guides:**

- **The callout had no "Show me" button, and showed "Next" on waiting steps.** So a waiting step could be
  Next-skipped and the escape hatch was unreachable by mouse (only the render called it). Fixed: an
  explanation step shows Next; a waiting step shows **Show me** (the escape hatch) and no Next, so the user
  must do the thing or ask the coach to. This also fixed guide 1's waiting step.
- **`PrepareStackBuilder` is idempotent** — it won't reset an already-open builder — so it can be the
  precondition on several consecutive steps without wiping the user's progress when they step forward.

**Anchors added:** `stack.new` (New-stack button), `stack.create` (the builder's Create button).

**Verified:** `captures/20260727-guide2-showme/guide2_step1..5`. Step 2 shows the builder open, both repos
auto-wired on the canvas; step 4 spotlights Create stack with a Show-me button; "Show me" saves the stack
and the wait advances to the handoff. The render fails if any step's anchor doesn't resolve.

The ladder now stands at guides 1–2 authored (register a repo; wire a multi-repo stack), 3–5 still planned
(run a workspace; the shared-port polyrepo case; drift/repair).

---

## 15. Guide 3 — create and run a workspace

The third guide, and the one that closes the loop: a stack is a plan, a workspace is the real, running,
isolated thing. Starts at the `StackWired` stage (a stack exists, nothing running) and walks: why a
workspace → create it → what sprig actually made → how you run and dispose of it.

**Shape.** Four steps. Only the create step waits. Creating a workspace is genuine work — a git worktree per
repo, port allocation, env/compose generation — so it runs async behind the app's normal progress checklist;
the store change it ends with (`NotifyStoreChanged`) advances the wait. The form-opening step drives the UI
in `Prepare` (same UI-vs-store pattern as guide 2), and pre-fills the form with **infra off**, so the guide
never depends on a Docker daemon.

**The payoff step.** After "Show me", the coach lands on the workspace detail with everything sprig produced
on screen: two worktrees on `sprig/feature-x` branches, ports allocated for this workspace alone
(8000/8001/8002), and the resolved `apiUrl` pointing at the API's real port — the abstract model made
concrete. The copy drives it home: your own repos never moved; this is a copy off to the side.

**Anchors added:** `workspace.new`, `workspace.create`. `workspace.detail` (from the tour) is reused for the
payoff step.

**Verified:** `captures/20260727-guide3/guide3_step1..4`. Step 2 shows the pre-filled form with Create
spotlit and a Show-me button; step 3 shows the created workspace `feature-x` with its two worktrees, ports,
and resolved values.

The ladder now stands at guides 1–3 authored — register a repo, wire a multi-repo stack, run a workspace:
the whole repo → stack → workspace journey, each stage its own hands-on lesson. Still planned: the shared-port
polyrepo case (hands-on), and drift/repair (needs a "break a worktree" sandbox action).

---

## 16. Guide 4 — recover from drift (the safety net)

The `doctor`/reconcile behaviour is sprig's quiet selling point, and it's impossible to show on a healthy
setup — so this guide *breaks* one on purpose. Starts at the `Running` stage, and its opening step deletes
one of the workspace's worktrees behind the user's back (a new `SampleSetup.BreakWorktree`), then reconciles
so the drift is on screen: `sample-api: worktree folder missing — run Repair`, `sample-web: ✓ in sync`.

**Shape.** Three steps: the break (explanation, spotlighting the drift), Repair (waiting), and the reassurance
finale. The user clicks Repair — or Show me — and the drift resolves.

**Honest about what Repair does.** Repair does *not* resurrect deleted work; for a missing folder it prunes
the stale git registration, taking the state from `MissingFolder` (drift) to `Gone` (a clean, known state).
So the completion predicate is `!HasDrift`, not "healthy", and the copy says "lines the record back up with
reality", not "rebuilds your worktree". Teaching the real behaviour is the whole point — the promise is *no
half-state is ever stuck*, not *nothing is ever lost*.

**The plumbing it needed:**

- `SampleSetup.BreakWorktree()` — deletes one repo's worktree folder (never the source repo), the sandbox
  action §12.5 flagged as the one rung that isn't just fixture staging.
- **Repair now fires `NotifyStoreChanged`** — it rebuilds/prunes worktrees, so reality changed; that's what
  lets the waiting step advance (reconcile stays read-only and silent, correctly).

**Anchors added:** `workspace.repair`. Verified: `captures/20260727-guide4-drift/guide4_step1..3` — step 1
shows the real drift, step 2 spotlights Repair, step 3 confirms recovery.

**Test-suite note:** the guide and sample tests spawn real `git` worktree operations; under parallel load a
worktree op occasionally hiccupped (one intermittent failure). They're now in a `git-heavy` collection that
runs them serially, which removes the contention.

The ladder now stands at four authored guides: register a repo, wire a multi-repo stack, run a workspace, and
recover from drift. The one still open from the original five is the **shared-port** polyrepo case — teaching
two repos deliberately sharing one port. It's the trickiest to hand-hold: sharing is a canvas *drag*, which
produces builder state rather than a store change, so a true waiting step there needs either UI-state polling
(the mechanism deferred in §12.5) or a Show-me-driven demonstration. A decision for when it's picked up.
