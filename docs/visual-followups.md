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
