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
