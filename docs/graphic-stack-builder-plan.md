# Graphical-first stack builder — assessment & phased plan

Turning stack creation into a canvas-first experience: name a stack, land on a canvas with a
**port rail on the left**, **repos on the right**, a **transform column in the centre**, and wire
them by dragging.

This builds on the existing `graphic`-branch work (`WiringCanvas`, `WiringGraph`, `TransformPresets`,
`StackAutowire`) rather than replacing it. See [`wiring-ux-plan.md`](./wiring-ux-plan.md) for the
prior "canvas as a second view" plan this supersedes.

---

## 1. Where we are today (so the plan reuses, not rebuilds)

- **`StackDefinition`** is `Repos` + `Ports` + `Bindings[repo][input] = expression` + `Shares`.
  `Bindings` is the **single source of truth** for resolution (`StackResolver → StackWiring`); the
  CLI, workspace-create, and every test read it. **We keep this untouched.**
- An expression is one of four shapes (see `BindingClassifier`): **Unbound**, **Identity**
  (`${sprig.ports.x}`), **Transform** (a port wrapped in text, or *multiple* ports), or **Literal**
  (no port token — this also covers `${sprig.workspace}`, which references no *port*).
- **`WiringCanvas`** already draws the graph and supports drag-to-wire, but today it drags an
  **input pin → port** (the reverse of your proposal), lays ports on the left / repos on the right,
  and is a toggle *inside* the form modal. Layout is deterministic and derived — **no positions are
  persisted**, which is a feature we should keep.
- **`StackAutowire`** already proposes the mechanical wiring; **`TransformPresets`** already
  generates/recognises the `http://localhost:${…}`-style expressions your transform node will edit.

**Implication:** the model is ready. This is a UI/interaction project, and the resolution engine,
CLI, and existing tests are out of scope for changes.

---

## 2. Assessment — glaring issues to resolve before building

### A. Model / conceptual (these bite hardest)

1. **Not every input is fed by a port.** A pure "drag a port onto an input" model has **no home
   for**:
   - bare **literals** (e.g. `production`, a hard-coded `http://localhost:4000`), and
   - **`${sprig.workspace}`** (a first-class producer variable that is *not* a port).

   This is the single biggest gap. The canvas needs (a) a **`workspace` source** on the left rail
   alongside ports, and (b) a way to give an input a **literal with no incoming line** — most
   naturally by typing directly into a transform/expression node (or an inline editor on the input
   pin). Without this, some real stacks simply can't be expressed on the canvas.

2. **The drag direction is reversed from today.** Current: input → port. Proposed: port → input.
   Both create the *same* binding, but the rubber-band, hit-testing, and drop semantics are a real
   rewrite, not a tweak. Decide the **replace rule**: dropping a second port-line on an
   already-bound input must *replace* (the repo side is single), ideally with a subtle confirm/undo.

3. **The transform node is the future fan-in point — model it that way now.** "Repo side can only
   have a single line" is true for *direct* lines, but your future "multiple ports → one transform"
   means the **transform node**, not the input pin, is where N ports converge and 1 value emerges.
   If we build the transform as mere per-line sugar (as today), the multi-input feature forces a
   redesign. Treat the centre node as **owning the expression and its ≥1 port inputs**; the input
   pin always has exactly one incoming thing: *a port line* **or** *a transform node*.

4. **"Ask for a variable name" is naming a new *port*.** In sprig a port has a name, an input is
   named by its repo, and a binding has no name of its own. So the post-drag prompt creates a **new
   port** (validated with the same `^[A-Za-z0-9._+-]+$` rule ports already use; reject duplicates).
   **Cancel must abort the line.** The phantom "create new…" slot must regenerate after each use.

### B. Affordance parity (everything the form does must have a canvas home, or stay reachable)

5. The form currently owns: **stack name**, **add/remove repo**, **add/remove/rename port**,
   **set a literal**, **auto-wire**, **import/export**, and the **edit-lock** ("N workspaces use
   this stack — remove them before editing"). Going canvas-only means each of these needs a canvas
   affordance *or* the form stays as an escape hatch during transition. Rename-port propagation is
   already implemented in the VM (`PropagatePortRename`) and must keep working from the canvas.

6. The transform node's editor should **reuse `SprigTokenBox` / `SprigTokenCompletion`** (the
   `${sprig...}` autocomplete already used in the form) inside a canvas flyout — don't reinvent it.

### C. Layout / rendering

7. **Three columns mean multi-segment cables.** A line may now route port → transform node → input
   instead of one bezier. Transform nodes need **vertical slotting** in the centre to avoid overlap,
   and cables become 2–3 segment paths. Crossing lines are already a known issue and get worse with
   a centre column — keep the existing hover-dim + tooltip as the tracing aid; treat
   crossing-minimisation as out-of-scope polish.

8. **Keep deterministic columnar layout — do *not* add free node positioning or position
   persistence.** Your design keeps everything in columns, so we get the graphical feel without a
   layout-storage/versioning problem. This is a deliberate simplification worth protecting.

### D. Scope / transition

9. **The canvas must reach 100% coverage so the form can be retired** (D2). Every case — literals,
   `workspace`, respecting per-input `AllowedPorts`, bulk edits, the edit-lock — must be
   canvas-native. The form survives only as a transition safety net through P1–P5 and is removed in
   P6; no configuration path may depend on it at the end.

10. **`WiringGraph` needs a modest extension**, not a rewrite: a `workspace` pseudo-source, a
    first-class transform-node concept (present when the expression is a Transform or references >1
    port), and clean multi-port edges. It stays a pure, derived, unit-tested projection of
    `Bindings` — so resolution and tests are untouched.

---

## 3. Decisions — **locked**

| # | Decision | Outcome |
|---|----------|---------|
| D1 | Where do literals & `${sprig.workspace}` live? | **The input pin holds the one expression; set it by (1) dragging a source onto it, (2) clicking to type inline (`SprigTokenBox`), or (3) a transform node.** No separate "Literals" section — literals are per-input and rarely shared. **`workspace` is a draggable built-in source** on the left rail (a fixed, named, fan-out producer — not a literal). |
| D2 | Fate of the existing form | **Canvas reaches 100% coverage; the form is retired (Phase 6).** The form survives *only* as a transition safety net during P1–P5, not as a permanent escape hatch. No configuration path may depend on it by the end. |
| D3 | Persist node positions? | **No — deterministic columnar layout.** No layout storage/versioning. |
| D4 | Second line onto a bound input | **Replace the existing binding**, with a brief undo affordance. |

### The unifying rule (from D1)

> Every input has **exactly one expression**. The port line, the transform node, and the inline
> editor are three interchangeable ways to write that one string. The canvas renders each input's
> current value inline, with an empty-state hint (*"drag a source here, or click to type"*) so the
> click-to-edit affordance is discoverable.

Left rail = **ports + the `workspace` chip + the phantom "create new…"**. *(Future, not now:
"promote a literal to a named source" if shared literals ever become common.)*

---

## Status (2026-07-25)

All phases **implemented, committed, and green** (380 tests). Rendering verified via headless PNGs;
pointer-driven interactions are logged in [`visual-followups.md`](./visual-followups.md). After the
user's live test-drive and three rounds of refinement, the form was retired (Phase 6): the canvas is
now the only stack-building surface.

| Phase | Commit | State |
|------|--------|-------|
| 0 Model shim | `966b077` | ✅ done |
| 1 Canvas-first shell | `384f901` | ✅ done |
| 2 Source→input drag | `082590b` | ✅ done |
| 3 Transform nodes + inline editor | `2be54dc` | ✅ done |
| 4 Port/repo management on canvas | `4c58720` | ✅ done |
| 5 Multi-input fan-in | `dd0ae58` | ✅ done |
| 6 Retire the form | done | ✅ done — canvas is the only builder |

**Dead-code cleanup done:** the form-only `BindingRow` display flags (tag booleans, collapse) and
the transform-preset module (`Port`/`SelectedTransform`/`CanTransform`/`SyncTransform`), plus
`RepoBindingGroup`'s collapse state, `ToggleGroup`, `RefreshClassification`/`ApplyTag`,
`SetPinTransform`, and the canvas `TransformCommand`/`TransformRequest` were removed. Wiring now
writes `BindingRow.Expression` directly (`WirePin`/`CreatePort` via a `PortToken` helper) and the
canvas rebuilds from it. `BindingRow` is now just `Input` / `Example` / `Expression`. Seven
form-only tests were removed; suite green at 373.

## 4. Phased implementation plan

Principles (carried from the prior plan): **`Bindings` stays the resolution source of truth**;
**all logic is unit-tested as it lands, visual verification deferred** to a follow-ups doc; each
phase ends green (`dotnet build` + `dotnet test`); commit locally only (no push, no branch delete).

### Phase 0 — Decisions locked + model shim *(no visible UI change)*
- Resolve D1–D4 above.
- Extend `WiringGraph` (pure, tested): add a **`workspace` source node**; add a derived
  **transform-node** per `(repo, input)` whose expression is a Transform or references >1 port;
  expose **multi-port edges** cleanly (today the pin collapses to a single port when
  `count == 1`).
- No `StackDefinition`/resolution change. New records + unit tests only.
- **Commit:** `Model: workspace source + transform nodes + multi-port edges in WiringGraph`

### Phase 1 — Name-first entry + canvas-primary shell
- "New stack" → a small **name dialog** → land on the **editable canvas** as the primary surface
  (not the form modal). Save / Cancel.
- Canvas shows the **port rail** (left, with a `+` and the phantom **"create new…"** slot), the
  **`workspace`** source, and **repos** (right, with a `+` to add a repo via the existing picker).
- Reuse the existing `BuilderWiring` plumbing (`RebuildBuilderWiring`, `WirePin/UnwirePin`); the
  form still exists behind an "Advanced" toggle (D2).
- **Commit:** `Canvas-first stack builder shell (name → canvas)`

### Phase 2 — Reverse the drag: **port → input**, create-on-drop
- Rewrite the canvas drag so you drag **from a port outlet to an input pin**: drop on an input
  **binds** (replace if already bound, per D4); a port **fans out** to many inputs.
- Drag from the phantom **"create new…"** → on drop, **prompt for a port name** (validated; cancel
  aborts) → create the port and bind it; regenerate the phantom slot.
- **Delete a line** (click the cable → ✕, or drag the input off). Command layer unit-tested.
- **Commit:** `Drag ports onto inputs; create-on-drop new ports`

### Phase 3 — Transform nodes in the centre column
- **Inline input editor (the D1 literal home):** click an input → a `SprigTokenBox` (autocomplete
  over ports + `workspace`) to type a literal, `${sprig.workspace}`, or any raw expression. Render
  each input's current value inline with the empty-state hint. This is the primary literal path.
- "Add transform" on a line → a **centre node** editing the *same* input expression when a port
  needs shaping; the cable routes **port → node → input**. Presets (`TransformPresets`) are
  one-click fills inside the node.
- Layout: vertical slotting of nodes, multi-segment cables.
- **Commit:** `Transform nodes: per-line expression editor in the centre column`

### Phase 4 — Port/repo management on the canvas + full parity
- **Rename port** inline on the port node (reusing `PropagatePortRename`); **remove** port/repo;
  **respect the edit-lock** (read-only canvas + banner when workspaces depend on the stack).
- **Auto-wire** button on the canvas (reuse `StackAutowire`); surface validation/errors.
- Verify the canvas now covers every form operation → the form can be demoted.
- **Commit:** `Full stack-editing parity on the canvas`

### Phase 5 — Multi-input transforms *(your "future" item)*
- Let a transform node accept **≥2 port lines** (fan-in), emitting one expression such as
  `${sprig.ports.a}:${sprig.ports.b}`. The editor already supports multiple tokens; this phase is
  the **wiring + layout** (drag a second port into an existing node), enabled by the Phase 0 graph
  work.
- **Commit:** `Multi-port transforms (fan-in) on the canvas`

### Phase 6 — Demote the form + polish
- Remove or fully demote the form (D2) once parity is proven.
- Optional polish: zoom/pan, crossing reduction, keyboard nav, empty states.
- **Visual verification pass** (screenshots — will ask before capturing, per house rules) and docs
  update (`user-guide.md`, `config-reference.md`).
- **Commit:** `Retire the form; canvas is the stack builder`

---

## 5. Sequencing & risk

- **Lowest risk:** Phase 0 (pure model, tested) and resolution staying frozen — the CLI and all
  existing tests are never touched.
- **Highest interaction risk:** Phase 2 (drag rewrite) and Phase 3 (multi-segment layout / literal
  home). These are where visual verification matters most.
- Each phase is independently revertable and shippable. The form staying reachable until Phase 6 is
  the safety net that lets us ship the canvas before it's 100% at parity.
