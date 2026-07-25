# Visual follow-ups (deferred UI verification)

The wiring-UX work ([`wiring-ux-plan.md`](./wiring-ux-plan.md)) was implemented while the requester
was away. All **logic** is unit-tested. The items below need a human to run the app and eyeball
them — they were deliberately deferred rather than verified with screenshots.

> How to check: `dotnet run --project src/Sprig.App`, open **Stacks → New stack**, add repos, and
> exercise each item. Tick items off as they pass; note anything that needs a tweak.

## M1 — Auto-wire

- [ ] **Auto-wire button** appears in the **Inputs** section header of the New/Edit stack modal, is
      disabled until at least one repo is selected, and enables once a repo is chosen.
- [ ] Selecting repos then clicking **⚡ Auto-wire** fills every empty binding with
      `${sprig.ports.<name>}` (or `http://localhost:${…}` for a URL-shaped input) and adds the
      matching ports to the Ports list, with sensible `≈` previews.
- [ ] A binding you typed by hand is **not** overwritten by Auto-wire.
- [ ] Two repos that each declare the same input (e.g. `dbPort`) get **distinct** ports
      (`db_port`, `db_port_2`) — not silently shared.

## M2 — Collapse / surface exceptions

- [ ] After Auto-wire, plain identity rows fold behind a **"N inputs auto-wired to matching ports —
      all valid ✓ ▸ review"** strip per repo; clicking it reveals them and flips to **▾ hide**.
- [ ] Rows that aren't plain identities stay visible with a tag chip: `transform` (pink), `shared`
      (blue), `literal` (grey), `needs value` / `unknown port` (red).
- [ ] Editing a token box live re-tags the row (e.g. typing a port token turns a literal into a
      transform/identity; pointing a second input at a port turns both `shared`).
- [ ] Chip colours read correctly in the dark theme and don't crowd the input name.

## M3 — Transform modules

- [ ] A binding that references exactly one port shows a **preset dropdown** beside its token box
      (Raw port / URL http / URL https / localhost:port / Custom…); it's hidden for literals and
      multi-port expressions.
- [ ] The dropdown opens on the **recognised** preset (an auto-wired `apiUrl` reads as "URL — http").
- [ ] Choosing a preset rewrites the token box over the same port; choosing **Custom…** leaves the
      expression alone for hand-editing.
- [ ] The dropdown sits comfortably next to the token box without overflowing the modal width.

## M4 — Explicit share + rename propagation

- [ ] Each binding row shows a **port picker** ("share a port…") listing the stack's ports; picking
      one binds the input to it. Picking a port another input already uses turns both rows `shared`.
- [ ] The **port** + **as** (transform) dropdowns sit on one line under the token box and wrap/fit
      within the 720-wide modal (check with 2–3 repos expanded).
- [ ] **Renaming a port** (edit the name, then Tab/click away) rewrites every binding that referenced
      it — no row is left showing `unknown port`. Renames commit on blur, not per keystroke.
- [ ] Saving a stack where two inputs point at one port and reopening it (Edit) still shows them as
      `shared` (the `shares` block persisted). Check the exported JSON contains a `shares` entry.

## M5 — Patchbay canvas

**Already verified (static render):** the canvas draws on real Skia without error — repos + pins
either side, green port rail, blue/pink cables, `SHARED ×2` highlight. Regenerate any time with
`dotnet run --project src/Sprig.App -- render <dir>`, which now emits `stacks_wiring_diagram.png`
(alongside the other view PNGs) into `<dir>`. The first run landed under `captures/` (gitignored).

Still needs a live window (can't be caught by a static capture):

- [ ] Stacks detail has a **Diagram / List** toggle; selecting a stack and toggling shows the
      patchbay; toggling back restores the REPOS/PORTS/INPUTS lists.
- [ ] **Hovering a port** dims every cable not on it (so a shared port's fan-out stands out); moving
      off restores them. (Interactive — not in the PNG.)
- [ ] Unbound / unknown-port inputs render with a **red** pin + label in a real stack that has them.
- [ ] The 440-high diagram panel scrolls (H/V) for a stack with many repos/ports without clipping.
- [ ] Legend row under the diagram matches the cable/port colours.

## Review-feedback fixes (round 2)

**Verified via headless render:** the ✕ on repo input rows is centred; the diagram takes the full
width with the stack list collapsed and the toggle reading "List" (see `main_stacks_diagram.png`).

Still needs a live window (flyout/interaction — not in a static capture):

- [ ] **Override flyout** (click a value in an env/compose override): now has 14px padding and a
      constrained width so the token box no longer overflows the popup.
- [ ] **Port auto-guess**: click an unset env key (or compose value) whose example hard-codes a local
      port — e.g. `Host=localhost;Port=5432` or `http://localhost:5000` — and the "OVERRIDE/REPLACE
      WITH" box opens pre-filled with the port templated to a declared input (only when the input is
      unambiguous). External hosts and `5432:5432` published ports are left alone.
- [ ] **Token box on expand**: expand a collapsed "▸ review" group in the stack builder — the first
      revealed binding box now shows its value immediately (no click needed).
- [ ] Auto-wire no longer produces `frontend-port_port`; an input already ending in a port word keeps
      its name (existing saved stacks keep their old names until re-wired).

## Diagram relayout (round 3)

**Verified via headless render:** ports now form a single left-hand rail with every repo stacked on
the right; cables flow left→right, and each `SHARED ×N` port visibly fans out to its consumers (see
`stacks_wiring_diagram.png`). Still needs a live window:

- [ ] **Hover a port** → the other cables dim and a tooltip appears listing every consumer
      (`repo · input`, transform consumers in pink). The tooltip follows the cursor and stays inside
      the panel edges.
- [ ] Crossing cables are inherent to a stack whose ports don't line up with their consumers — the
      hover dim + tooltip is the way to trace them. (Crossing-minimisation is a possible future
      enhancement, not done here.)

## Interactive canvas (round 4) — drag to wire

**Verified via headless render:** the stack builder has a **Form / Diagram** toggle in the modal
header; switching to Diagram widens the modal and shows the editable canvas bound to the builder's
live state (see `main_stacks_builder_diagram.png`). The wiring commands are unit-tested
(WirePin/UnwirePin/SetPinTransform + live graph).

Needs a live window (drag interactions can't be captured statically — please verify these):

- [ ] **Drag an input pin onto a port** → binds it (a dashed rubber-band cable follows the cursor
      while dragging; the target port shows a highlight ring on hover; on drop a real cable appears).
- [ ] **Drag a bound pin onto empty space** → unbinds it (the cable disappears; the pin/label go red).
- [ ] **Click a bound pin** (no drag) → a menu opens with the transform presets (Raw port / URL http /
      URL https / localhost:port) and **Unbind**; picking one rewrites the binding over its port.
- [ ] Edits on the canvas are reflected in the **Form** view (toggle back) and vice-versa — they edit
      the same rows.
- [ ] Wiring two inputs to one port shows it `SHARED ×2` immediately.
- [ ] Repos/ports are still added in the **Form** view; the diagram is the wiring surface. (Adding
      ports/repos from the canvas is a possible future enhancement.)
- [ ] The canvas actually receives pointer events (relies on `ICustomHitTest`) — if drag does nothing,
      that's the thing to check first.

## Graphical-first builder ([`graphic-stack-builder-plan.md`](./graphic-stack-builder-plan.md))

### Phase 1 — canvas-first shell

**Verified via headless render** (`main_stacks_builder_diagram.png`): New stack opens on the canvas
with a persistent **Name** field on top and a **⚙ Advanced (form)** toggle; the violet **workspace**
SOURCE node sits at the top of the rail. Read-only detail diagram unchanged (workspace shows only
when used). Still needs a live window:

- [ ] The phantom **"create new…"** slot renders at the bottom of the rail (scroll down on a tall
      stack; visible without scrolling on a 1–2 port stack).
- [ ] Toggling **⚙ Advanced (form)** ⇄ **◨ Canvas** swaps surfaces and keeps the Name field.
- [ ] A stack that binds an input to `${sprig.workspace}` draws a violet cable from the workspace
      node to that input (and `SOURCE ×N` when shared).

### Phase 2 — drag ports onto inputs (create-on-drop)

**Logic unit-tested** (CreatePort / WireWorkspace / replace-on-rebind, +5 tests). Drag gestures
can't be captured statically — please verify in a live window:

- [ ] **Drag a port outlet onto an input** → binds it (dashed rubber-band follows the cursor; the
      target input shows a ring; on drop a real cable appears). A port dragged to several inputs
      fans out.
- [ ] **Drag the workspace source onto an input** → binds it to `${sprig.workspace}` (violet
      rubber-band + cable).
- [ ] **Drag the "create new…" slot onto an input** → a small text box pops to name the port; Enter
      creates it and wires the input; **Escape cancels** (no line, no port). Typing an existing
      name reuses that port instead of duplicating.
- [ ] **Drop a source on an already-bound input** replaces its binding (repo side is single).
- [ ] **Drag a bound input off onto empty space** unbinds it (red rubber-band); **click a bound
      input** opens the transform/Unbind menu.
- [ ] Dropping a source on the rail sentinels (workspace/create) or empty space is a harmless no-op.

### Phase 3 — transform nodes in the centre column + inline editing

**Verified via headless render** (`stacks_wiring_diagram.png`): a transform binding (`apiUrl`) draws a
centre-column **ƒ node** showing its expression, with a blue source cable (`api_port → node`) and a
pink `node → input` segment; identity bindings still run straight port→input. Still needs a live
window:

- [ ] A **pure literal** input shows its value inline (right-aligned, muted) with no cable/node.
- [ ] **Click an empty input** → a `SprigTokenBox` flyout with `${sprig...}` autocomplete; typing a
      literal / `${sprig.workspace}` / raw expression and pressing **Set** binds it.
- [ ] **Click a ƒ transform node** → the same editor opens on its expression; editing rewrites it and
      the node text updates.
- [ ] **Click a wired input** → menu now has **Edit expression…** alongside the transform presets and
      Unbind.
- [ ] A multi-port expression routes both source cables into one ƒ node (fan-in), one node→input
      segment out. (Wiring a second port into an existing node is Phase 5.)

### Phase 4 — port/repo management on the canvas

**Verified via headless render** (`main_stacks_builder_diagram.png`): the canvas toolbar shows
**＋ Add repo** and **⚡ Auto-wire**. Logic unit-tested (add/rename/remove port, +3). Still needs a
live window:

- [ ] **Click a port** (no drag) → a menu with **Rename…** / **Remove port**; Rename pops a prefilled
      box that rewrites every binding using it; Remove drops the port and its cables.
- [ ] **Click "create new…"** (no drag) → a box to add a bare port (no wiring).
- [ ] **＋ Add repo** opens a checkbox flyout; checking adds a repo (its inputs appear), unchecking
      removes it. **⚡ Auto-wire** fills unbound inputs from the canvas.
- [ ] Editing is still gated upstream (the builder only opens for a stack no workspace depends on),
      so there is no locked-canvas state to reach here.

### Phase 5 — multi-input transforms (fan-in)

**Verified via headless render** (`stacks_wiring_diagram.png`, sample now includes a two-port
`dbAddr`): two source cables converge on one ƒ node, one node→input segment out, and the reused
ports show `SHARED ×3` / `×2`. Logic unit-tested (append + dedupe, +2). Still needs a live window:

- [ ] **Drag a port onto an existing ƒ node** → its token is appended to the node's expression (the
      node highlights as the drop target while hovering); a second cable joins the fan-in.
- [ ] Dragging a port already feeding the node is a no-op (no duplicate token).
- [ ] Refine separators/text by clicking the node (the inline editor from Phase 3).

### Feedback round 1 (canvas refinements)

**Verified via headless render** (`main_stacks_builder_diagram.png`): three column headers
(**SOURCE / TRANSFORM / REPO**) aligned to the columns, with **⚡ Auto-wire** under TRANSFORM and
**＋ Add repo** under REPO (the old toolbar is gone; the workspace node's SOURCE tag moved to the
header). Still needs a live window:

- [ ] **Drag an input back to a source** wires it (blue rubber-band + a ring on the target source);
      onto **workspace** (violet) or a **port**; onto **create new…** quick-adds a named port from the
      repo side; dropping onto empty still unbinds (red). Re-wiring replaces the current binding.
- [ ] **Click a cable** selects that binding: a ring on its input plus **ƒ** (add/change transform)
      and **✕** (delete) buttons appear just left of the input. **✕** unbinds; **ƒ** opens the
      transform/edit menu. Clicking empty space clears the selection.
- [ ] Header buttons work: **Auto-wire** fills unbound inputs; **Add repo** opens the checkbox
      picker.
- [ ] Column headers stay aligned to the canvas columns at the modal's normal width (they don't
      track horizontal scroll on a very narrow window — minor).

### Crash fix — Auto-wire (round 4a)

Auto-wire crashed the app on the real Windows backend (not reproducible headless): the compositor
could commit a render between a `BuilderWiring` change and the canvas re-measure, so `Render` looked
up a port in a stale layout dictionary (`KeyNotFoundException: 'port'`, WiringCanvas.Render). Fixed by
rendering from the exact graph snapshot the layout was built from (`_laidOut`) plus `TryGetValue`
guards, so the render thread can never throw on a transient mismatch.

- [ ] **Regression check:** open the stack builder, select repos, add a port, click **⚡ Auto-wire** —
      it should fill the bindings without crashing (previously crashed here).
