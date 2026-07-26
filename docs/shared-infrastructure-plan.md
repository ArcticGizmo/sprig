# Shared infrastructure — phased implementation plan

Companion to the design exploration in [`shared-infrastructure-ux.html`](./shared-infrastructure-ux.html)
(rev 2). That document argues for pooling docker resources across workspaces by varying the **database
name** instead of the port — and for doing it as a **machine-local overlay** rather than a change to any
file you share with your team. This plan turns that into shippable milestones.

## Decisions (locked)

- **An overlay is a transform over the resolved plan**, not a third producer. Repos and stacks never
  reference a shared resource; sprig applies the overlay itself, machine-locally, after the stack has
  produced every value and before anything is written to disk. Data still flows one way, stack → repo.
- **No schema change to anything shared.** `.sprig.json` stays at schema 2. `StackDefinition` stays at
  schema 2. The only new file is `shared/<name>.json` in the central store, plus two new fields on the
  instance record.
- **Precedence is `repo → stack → shared`**, strictly. A later layer may overwrite an earlier one; nothing
  writes upward. Two overlays writing the same target is a hard error, not last-writer-wins.
- **Off must always be a working configuration.** An overlay may only *replace a value that already
  resolves* — never supply a missing one. Enforced in the validator, not trusted.
- **Prefer the highest layer that can express the change.** Override `bindings[repo][input]` when the repo
  exposes a suitable input; only reach down into `env[].set` when it doesn't.
- **All logic is unit-tested as it lands.** Visual verification of the app surface is deferred to M5.

## Invariants that keep the layer cake honest

> **I1** — Removing every overlay from a plan yields exactly the plan sprig would have built without the
> feature. (Guarantees `--no-shared`, `enabled: false`, and "your teammate never finds out" all work.)
>
> **I2** — An overlay op whose target does not resolve is a hard failure at plan time, never a silent skip.
>
> **I3** — A workspace materialises against the overlay set **pinned on its instance record**, not against
> whatever is enabled now.

---

## M0 — Foundation: the plan object, and splitting resolve from allocate

**Goal:** introduce the object an overlay can transform, and stop allocating ports before we know which
ones survive. No overlays yet, no docker, no behaviour change beyond port GC.

- [x] `Sprig.Core/Planning/PlanLayer.cs` — `Repo | Stack | Shared`.
- [x] `PlanNote` + `PlanTargets` — one recorded decision (layer, target, value, what it replaced, source).
      Target strings are stable and parseable: `input:<name>`, `env:<file>#<key>`,
      `compose:<file>#<a.b.c>`, `port:<name>`.
- [x] `WorkspacePlan` / `PlannedRepo` — per repo: the source `ResolvedRepo`, an **effective
      `SprigRepoConfig`** (what overlays edit), and the input→expression bindings. Plus declared ports and
      a computed `ReferencedPorts`.
- [x] `BoundPlan` / `BoundRepo` — the plan with ports allocated: resolved input values, a ready
      `IVariableSource` scope per repo, and notes with values resolved.
- [x] `WorkspacePlanner.Plan(stack, workspace)` — builds the plan, hard-failing on an unbound input
      (takes that responsibility over from `StackWiring`).
- [x] `WorkspacePlanner.Bind(plan, allocatedPorts)` — resolves every binding expression.
- [x] `WorkspaceService.Create` reordered: **plan → constraints → allocate referenced ports → bind →
      materialise**, with env/compose/setup all reading `EffectiveConfig`.
- [x] `PlanCreate` derives its checklist from the plan, so the rows a UI pre-renders match what create
      actually does once overlays exist.
- [x] Port GC: a declared stack port that no surviving binding references is **not allocated**. Small,
      deliberate behaviour change — an unreferenced port did nothing before either.
- [x] `sprig plan --stack <name> [--name <workspace>]` — dry-run a create and print every value with its
      layer. Useful today, essential once a hidden layer exists.
- [x] Tests: plan shape, unbound-input failure, port GC, notes carry layer + expression + value,
      `--json` output.

**Commit:** `Add the workspace plan object and split resolve from allocate`

---

## M1 — The overlay engine

**Goal:** shared-resource definitions that rewrite a plan. Still no docker — this milestone is entirely
deterministic and unit-testable.

- [ ] `SharedResourceDefinition` + `SharedResourceStore` (`%LOCALAPPDATA%\sprig\shared\<name>.json`):
      `name`, `enabled`, `capacity`, `whenIdle`, `compose`, `port`, `values`, `attach`, `detach`,
      `injects[]`.
- [ ] `injects[]` shape: `{ repo, inputs{}, env[], suppress[] }` — `repo` doubles as applies-to.
- [ ] `SharedScope` — resolves `${sprig.shared.<value>}` against the resource's own `values`, which may
      themselves reference `${sprig.workspace}`.
- [ ] `OverlayEngine.Apply(plan, resources)` → new plan + notes:
      - replace `bindings[repo][input]` (preferred layer);
      - replace/add `env[file].set[KEY]` on the effective config;
      - replace/add `compose[file].overrides[path]`.
- [ ] **I2** — every op's target must resolve or it's a `SharedResourceException` naming the resource, the
      repo, and the target. `optional: true` opts out per-op.
- [ ] **Conflict detection** — two enabled overlays writing one target is a hard error naming both.
- [ ] **I1 validator** — an overlay may not introduce an input the repo doesn't declare, nor an env file
      the repo doesn't already target unless explicitly `add: true`.
- [ ] Forbidden targets: `setup[]`, worktree path, branch.
- [ ] `sprig plan` renders `shared` notes with strikethrough originals; `--no-shared` skips the engine.
- [ ] Tests: precedence, conflict, unresolved target, I1 round-trip (apply then strip == unlayered plan).

**Commit:** `Add the shared-resource overlay engine`

---

## M2 — Compose suppression and pruning

**Goal:** stop the repo's own postgres from starting. Structural compose edits, not just scalar overrides.

- [ ] `ComposeGenerator` learns removal: drop `services.<name>`, prune it from every other service's
      `depends_on` (both list and map forms), drop volumes/networks left unreferenced.
- [ ] A compose file whose every service is suppressed generates **no file at all**.
- [ ] `suppress[]` flows from the overlay into the effective config and out through the plan.
- [ ] Tests: dangling `depends_on` pruned, orphaned volume dropped, emptied file skipped, an untouched
      compose file is byte-identical to today's output.

**Commit:** `Suppress overlay-provided services from generated compose`

---

## M3 — Slots, refcount, attach and detach

**Goal:** the part that actually saves resources — and the riskiest, so it gets integration tests against
a real postgres.

- [ ] `SharedLeaseStore` (`shared/leases.json`) on the same file-lock pattern as `FilePortStore`.
- [ ] **Counter 1 — attached** (`create` → `rm`): slot reserved at create, capacity enforced *before* any
      worktree exists, released at rm.
- [ ] **Counter 2 — running** (`up` → `down`): refcount over attached workspaces that are up; 0→1 starts
      the shared project, 1→0 stops it (volumes kept).
- [ ] Shared compose runs as its own project `sprig-shared-<name>`; its host port is leased from the
      existing port ledger under the pseudo-workspace `@shared/<name>`.
- [ ] `attach` / `detach` command execution inside the container, with healthcheck waiting and a timeout.
- [ ] **I3** — `appliedOverlays` and `slots` pinned onto `InstanceRecord` at create and read from there for
      the workspace's whole life. Editing a resource is blocked while any workspace references it.
- [ ] `--no-shared` on create; `down --volumes` never touches shared volumes; `reset` = detach + re-attach
      this slot only.
- [ ] The "full" error: holders listed oldest-first, the one-line model explanation, three ways out.
- [ ] Multi-repo namespace collision detected at plan time (R5).

**Commit:** `Add shared-resource slots, refcounting and attach/detach`

---

## M4 — Extraction, presets, injection-point selection

**Goal:** nobody hand-authors override rules.

- [ ] `SharedResourceExtractor` — lift a service (plus its volumes/networks) out of a repo compose file
      into a resource definition, recording provenance.
- [ ] Preset library keyed on image: postgres, mysql, redis, mongo. Fills `values`, `attach`, `detach`,
      health, and default capacity.
- [ ] **Injection-point selection** — for each value the repo needs back, pick the highest layer that can
      carry it and record *why*, so the UI and CLI can explain the choice.
- [ ] `sprig shared extract --repo <name> --service <svc>`, `sprig shared list|show|enable|disable|rm`.
- [ ] Tests: extraction round-trip, preset detection, injection point chosen at the input layer when an
      input exists and the env layer when it doesn't.

**Commit:** `Extract shared resources from repo compose files`

---

## M5 — The app surface, layer chips, and doctor

**Goal:** make the hidden layer discoverable, and safe to leave running for a month.

- [ ] **Set up → Shared** — list + detail: capacity meter, slot table, "what it changes", "reaches",
      start/stop/disable/delete with the typed-name confirmation.
- [ ] Extract overlay on the repo editor's compose surface.
- [ ] **Layer chips everywhere** — the plan view on workspace detail, and "overridden on this machine"
      markers in the repo and stack editors (the reverse view that makes R1's coupling visible from both
      ends).
- [ ] Create preview shows the plan diff; "Create without shared resources" button.
- [ ] `doctor`: reclaim slots whose workspace is gone, flag databases with no slot, stop a container with a
      zero refcount, and **re-validate every enabled overlay's targets against current repo configs**.
- [ ] Stale-slot nudge when a resource is at capacity.
- [ ] Docs: `config-reference.md` gains a shared-resource section; `user-guide.md` gains a walkthrough.

**Commit:** `Add the Shared page and layer provenance to the app`

---

## Risks carried from the design doc

| | Risk | Handled in |
|---|---|---|
| **R1** | Overlay couples to repo internals an owner may rename | M1 (hard fail) + M5 (doctor, reverse view) |
| **R2** | Port allocation must move before overlays can GC ports | M0 |
| **R3** | A workspace must remember the overlays it was built with | M3 |
| **R4** | Two overlays, one target | M1 |
| **R5** | Two repos in one workspace resolving to one database | M3 |
| **R6** | Discoverability of a hidden machine-local layer | M0 (`sprig plan`) + M5 (chips everywhere) |
