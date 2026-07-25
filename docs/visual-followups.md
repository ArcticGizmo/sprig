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
