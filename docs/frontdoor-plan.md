# Sprig — Front Door Implementation Plan

A milestone-based, phased plan to give sprig a **user-centric front door**: reframe the app
around the user's journey — *nothing → a running isolated instance* — instead of around the
three tables in the central store.

This plan is the build breakdown of the design proposal in
[`frontdoor-proposal.html`](./frontdoor-proposal.html) (open it in a browser). Artefact letters
below (A–G) map 1:1 to that proposal.

---

## 0. Framing & guiding principles

- **UI / IA only — the engine is untouched.** Everything here lives in `Sprig.App`.
  `Sprig.Core` (and the CLI) are not modified. No new persisted state, no schema change.
- **No vocabulary change (confirmed).** "Repo", "Stack", "Workspace" stay exactly as they are —
  they're load-bearing across the CLI, `.sprig.json`, and the docs. The front door adds a
  *journey layer on top*; it does not rename the schema.
- **Additive, not a replacement.** The three existing pages remain for power users. We add a
  **Home** page and make the nav read as a sequence.
- **State-driven, read-only.** Everything on Home is a pure projection of the stores the app
  already loads — the repo registry, stack store, and instance store. Deriving "where am I / what
  next" reads counts; it writes nothing.
- **Each milestone is independently shippable** and verified the M6 way: headless-render snapshots
  (`Rendering/HeadlessRenderer.cs`) plus ViewModel unit tests. No logic in the view layer.

---

## 1. Where it plugs in

| Area | File(s) today | Change |
|---|---|---|
| Page list + default landing | `ViewModels/MainWindowViewModel.cs:22-28` (`Pages`, `_currentPage = Pages[0]`) | Add **Home**; make it `Pages[0]`; reorder the rest to workflow order. |
| Left nav chrome | `Views/MainWindow.axaml` (208px nav, logo, tagline, page list) | Add Home item + **Set up** / **Run** group labels + per-page counts. |
| VM→View resolution | `ViewLocator.cs` | Resolve `HomeViewModel` → `HomeView` (name convention — nothing to change if named per convention). |
| **New** Home page | — | `Views/HomeView.axaml` + `ViewModels/HomeViewModel.cs`. |
| **New** journey state | — | A small `SetupState` projection (§2) consumed by Home + the nav counts. |
| Empty states | `ReposView.axaml:25-27`, `StacksView.axaml:19-21`, `WorkspacesView.axaml:41-45` | Make downstream ones point upstream with an inline action (E). |
| Wizard (reuses existing modals) | Add-repo modal (`ReposView`), New-stack overlay `StacksView.axaml:62-157`, New-workspace overlay `WorkspacesView.axaml:150`, `Core/Init/InitInspector.cs` | Sequence them behind a stepper (D). |

The counts and lists Home needs are already loaded by `ReposViewModel`, `StacksViewModel`, and
`WorkspacesViewModel` (each reads its store via `AppServices`). Home reuses those services; it
introduces no new data source.

---

## 2. Shared model — `SetupState` (binding decision)

One small, read-only projection drives both the journey rail and the next-best-action banner, so
they can never disagree:

```
SetupState {
  int Repos; int Stacks; int Workspaces;     // counts from the three stores
  Stage Stage;                               // Empty | ReposReady | StackReady | Running
  NextAction Next;                           // the single recommended step + its CTA + target page
}
```

**Next-best-action state machine** (the one obvious right click, in every state):

| Repos | Stacks | Workspaces | Stage | Next best action | Opens |
|---|---|---|---|---|---|
| 0 | – | – | `Empty` | **Add a repo** | Repos / wizard |
| ≥1 | 0 | – | `ReposReady` | **Wire a stack** *(or "Isolate just this repo" → G)* | Stacks |
| ≥1 | ≥1 | 0 | `StackReady` | **Spin up a workspace** | Workspaces (new) |
| ≥1 | ≥1 | ≥1 | `Running` | **New workspace** + recent list | Workspaces (new) |

This table is the single source of truth for Home's banner, the rail's "next" highlight, and the
first-run CTA. Unit-testable in isolation with no UI.

---

## 3. Milestones

### Phase 1 — The front door

#### M1 — Navigation reframe
**Goal:** the app stops landing new users on the last step of the pipeline.
- **Scope:** add a `Home` page (shell only) and make it the default landing page. Reorder the nav
  to workflow order — **Home · [Set up] Repos, Stacks · [Run] Workspaces** — with the two group
  labels and a live count on each page item. No behaviour on Home yet beyond the model picture +
  a link into the wizard/first action.
- **Exit criteria:** app opens on **Home**; nav renders in dependency order with correct counts;
  every existing page still reachable and unchanged; headless render of the shell matches the
  before/after in the proposal; VM tests green.

#### M2 — Home cockpit (Artefacts B + C)
**Goal:** Home answers "where am I and what's next" in both first-run and configured states.
- **Scope:** build `SetupState` (§2) and wire it to:
  - **B — journey rail:** three nodes (Repos → Stacks → Workspaces) with done/next/to-do state
    and live counts. Numbering is a real ordered dependency, so it's shown.
  - **C — next-best-action banner:** driven by the §2 table.
  - Configured extras: a **recent workspaces** panel (name, stack, ports, infra up/stopped pill —
    all already on the `InstanceRecord`) and a **quick actions** column.
  - First-run state: the model picture + single primary CTA + "walk me through setup" link.
- **Exit criteria:** Home reflects real store state; the banner points at the correct step in all
  four §2 states (unit-tested); headless renders of the first-run and configured Home match the
  proposal; clicking a rail node / banner navigates to the right page.

### Phase 2 — Teaching the model in-flow

#### M3 — Upstream-aware empty states + mental-model card (Artefacts E + F)
**Goal:** a stuck user is never told to do something they can't yet do, and can learn *why* the
pieces relate without leaving the app.
- **Scope:**
  - **E:** rewrite the downstream empty states to point **upstream** with an inline action —
    Stacks-with-no-repos → "Stacks compose registered repos — you have none yet" + an **Add a
    repo** button that opens the Repos flow; Workspaces-with-no-stack likewise. Keep copy
    user-side.
  - **F:** the producer→consumer "one picture" (from the README / `history/objective.md`) as a
    dismissible card on Home and behind a persistent `?` affordance.
- **Exit criteria:** each downstream empty state links to its upstream action and the action works;
  the model card renders and dismisses; headless renders captured; no new flows introduced.

### Phase 3 — Guided setup

#### M4 — "Set up sprig" wizard (Artefact D)
**Goal:** a brand-new user can go nothing → running without having to discover the tab order.
- **Scope:** a skippable stepper that **sequences the existing modals** — Add repo(s) → **Prepare
  configs** (run `InitInspector`'s proposal inline for any repo lacking a `.sprig.json`) → Build a
  stack → Spin up — with a progress spine. Launched from the first-run Home CTA; escape hatch to
  jump into any tab at any step. Reuses the modal bodies; the wizard is orchestration + the inline
  `init` step (today's dead-end when you register a not-yet-ready repo).
- **Exit criteria:** completing the wizard against the example repos yields a running workspace
  without touching the raw tabs; each step is skippable; the `init` step writes a valid
  `.sprig.json`; headless render of a mid-wizard step captured; VM tests for step gating green.

### Phase 4 — Fast path (optional)

#### M5 — Single-repo fast path (Artefact G)
**Goal:** let a first-timer feel the payoff in one step, before learning stacks and bindings.
- **Scope:** surface the engine's existing ad-hoc path (`WorkspaceService.Create` /
  `create --repo <path>`, the CLI's `ResolveSingleRepo`) as a one-click **"Isolate just this
  repo"** on Home for a registered zero-input repo. No stack required.
- **Exit criteria:** a single zero-input repo → running workspace in one action, no stack authored;
  the created workspace behaves identically to a stack-created one in the Workspaces page.

---

## 4. Testing strategy

- **Unit (VM / model):** `SetupState` stage + next-action transitions across all four §2 states;
  wizard step-gating (can't advance past "build a stack" with no repos). Pure, fast — the pattern
  the M6 VM tests already use.
- **Headless render** (`Rendering/HeadlessRenderer.cs`, per `history/m6-verification.md`): the
  reframed shell/nav; first-run Home; configured Home; each rewritten empty state; a mid-wizard
  step. Snapshots are the visual acceptance record.
- **No Core / CLI test changes** — nothing in `Sprig.Core` moves.

---

## 5. Non-goals

- **No engine or CLI changes**, and **no schema/vocabulary rename** (Repos/Stacks/Workspaces stay).
- **No new persisted state** — Home is a projection of the existing stores.
- **Not removing the existing tabs** — power users keep direct access.
- Networked/team onboarding, telemetry, or a hosted "getting started" tour are out of scope.

---

## 6. Suggested build order

Ranked clarity-per-effort (from the proposal's closing section):

1. **M1** — nav reorder + Home shell. Small, pure XAML/VM; kills the "lands on the last step" bug
   on its own.
2. **M2** — the rail + next-best-action. Read-only projections; this *is* the front door.
3. **M3** — empty states + model card. Copy and one small reusable control.
4. **M4** — the wizard. Highest effort, best first-run; orchestration over existing modals.
5. **M5** — single-repo fast path, if wanted. Exposes an engine path the CLI already has.
