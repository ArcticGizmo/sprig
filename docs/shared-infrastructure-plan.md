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

- [x] `SharedResourceDefinition` + `SharedResourceStore` (`%LOCALAPPDATA%\sprig\shared\<name>.json`):
      `name`, `enabled`, `capacity`, `whenIdle`, `compose`, `allowedPorts`, `values`, `attach`, `detach`,
      `injects[]`. Unknown keys are rejected on save — an ignored key in an overlay is an override that
      silently never fires.
- [x] `injects[]` shape: `{ repo, inputs{}, env[], compose[], suppress[] }` — `repo` doubles as applies-to.
- [x] Resource values are published into each injected repo's scope as `${sprig.shared.<value>}` and
      `${sprig.shared.<resource>.<value>}`, left as raw templates so substitution still happens in exactly
      one place (bind). `${sprig.repo}` is available alongside `${sprig.workspace}`.
- [x] `OverlayEngine.Apply(plan, resources)` → new plan + notes:
      - replace `bindings[repo][input]` (preferred layer);
      - replace/add `env[file].set[KEY]` on the effective config;
      - replace/add `compose[file].overrides[path]`;
      - collect `suppress[]` onto the plan (applied in M2).
- [x] **I2** — every op's target must resolve or it's a `SharedResourceException` naming the resource, the
      repo, and the target. `add: true` is the deliberate opt-out.
- [x] **Conflict detection** — two enabled overlays writing one target is a hard error naming both.
- [x] **I1 validator** — an overlay may not introduce an input the repo doesn't declare, nor an env file or
      compose path the repo doesn't already target unless explicitly `add: true`.
- [x] Forbidden targets: `setup[]`, worktree path, branch — structurally, the injection shape can't name them.
- [x] `PortConstraintResolver` skips inputs an overlay has taken over: an overridden input no longer feeds
      a stack port, so its `allowedPorts` has nothing left to constrain.
- [x] `sprig plan` renders `shared` notes with the displaced value; `--no-shared` skips the engine on both
      `plan` and `create`. `sprig shared ls|show|enable|disable|rm` (extraction is M4).
- [x] Tests: precedence, conflict, unresolved target, disabled/unreachable no-ops, the repo's own config
      never mutated, `--no-shared` reproducing the unlayered workspace, end-to-end create.

**Commit:** `Add the shared-resource overlay engine`

> **Note.** After M1 an overlay rewrites values but nothing manages the container behind them. A resource
> defined now will point a workspace at infrastructure that isn't running until M3 lands.

---

## M2 — Compose suppression and pruning

**Goal:** stop the repo's own postgres from starting. Structural compose edits, not just scalar overrides.

- [x] `ComposePruner`: drop `services.<name>`, prune it from every other service's `depends_on` (both list
      and map forms), and drop the volumes/networks the suppression **orphaned**.
- [x] Orphaned means *referenced before, unreferenced after* — a volume that was already unused stays put.
      Suppression removes what it broke; it is not a licence to tidy up the rest of someone's compose file.
- [x] Overrides run **before** pruning, so a repo's perfectly valid override of a soon-to-be-removed
      service doesn't become a "path not found".
- [x] A compose file whose every service is suppressed generates **no file at all** (`GenerateToFile`
      returns null and the path never reaches the instance record).
- [x] `suppress[]` flows overlay → plan → `BoundRepo.Suppress` → generation.
- [x] Tests: both `depends_on` forms, orphaned vs pre-existing-unreferenced volumes, network dropped when
      its last user goes, emptied file skipped, a missing service named clearly, and no-suppression output
      byte-identical to before.

**Commit:** `Suppress overlay-provided services from generated compose`

---

## M3 — Slots, refcount, attach and detach

**Goal:** the part that actually saves resources — and the riskiest, so it gets integration tests against
a real postgres.

- [x] `SharedLeaseStore` (`shared/leases.json`) on the same file-lock pattern as `FilePortStore`.
- [x] **Counter 1 — attached** (`create` → `rm`): slot reserved at create, capacity enforced *before* any
      worktree exists, released at rm. **Decided:** create→rm, so a stopped workspace keeps its database.
- [x] **Counter 2 — running** (`up` → `down`): 0→1 starts the shared project, 1→0 stops it (volumes kept).
      **Decided:** "is anyone else running?" is answered by **asking docker**, not by reading `LastStatus` —
      a record says what sprig last did, not what is true, and a crash would otherwise strand a container.
- [x] Shared compose runs as its own project `sprig-shared-<name>`, so a workspace teardown can never take
      it with it.
- [x] `attach` / `detach` run via `docker compose exec -T <execService> sh -c`, after `up --wait`, retried
      with backoff — `--wait` reaches "running or healthy", which for a database without a declared probe
      is not yet "accepting connections".
- [x] **I3** — `appliedOverlays` and `slots` pinned onto `InstanceRecord` at create and read from there for
      the workspace's whole life.
- [x] `--no-shared` on create; `down --volumes` never touches a shared volume.
- [x] The "full" error: holders oldest-first, the one-line model explanation, three ways out — and stale
      slots reclaimed *before* declaring full, so a phantom lease can't cost you a real one.
- [x] Multi-repo namespace collision detected at attach time, naming `${sprig.repo}` as the fix (R5).
- [x] `sprig shared up|down|reclaim`; `down`/`rm` refuse while workspaces are attached unless forced.
- [x] Tests: slot reuse, capacity message, reclamation, attach/detach commands, refcounted stop,
      `whenIdle: keep`, ordering of up, `--volumes` safety, failed-attach rollback, and I3 under a toggle.

**Commit:** `Add shared-resource slots, refcounting and attach/detach`

> **Deferred to M5:** a real end-to-end run against a live postgres. Every path here is covered by the
> fake docker service, as the rest of sprig's docker paths are.

---

## M4 — Extraction, presets, injection-point selection

**Goal:** nobody hand-authors override rules.

- [x] `SharedResourceExtractor` — lift a service (plus the volumes that hold its data) out of a repo
      compose file into a resource definition, dropping the parts that belonged to the repo
      (`container_name` would collide; its `depends_on` stayed behind).
- [x] Preset library keyed on image: postgres, mysql, redis, mongo. Fills `values`, `attach`, `detach`,
      `execService`, and default capacity. The name comes from the image tag (`postgres-16`) so version
      skew produces two pools rather than one wrong one.
- [x] **Credentials are read off the lifted service**, not asserted. The container initialises with
      whatever env the repo gave it; a preset that claims a username the image never created produces a
      resource whose own attach command can't log in. *(Found by running it against real docker.)*
- [x] **The host port is leased from the port ledger** under `@shared/<name>`, not the service's
      conventional number — a machine already running postgres has 5432 taken by something sprig can't
      see. *(Also found by running it.)*
- [x] **Injection-point selection** — the port input is read from the repo's own compose override rather
      than guessed from a name; the namespace override reaches into the env template only where no input
      carries it. Each choice records *why*.
- [x] A connection string sprig can't confidently rewrite becomes a **warning, not a guess** — the failure
      it prevents is four workspaces silently sharing one database.
- [x] `sprig shared extract --repo <r> --service <s> [--yes]` — prints the proposal and writes nothing
      without `--yes`.
- [x] Tests: extraction shape, preset/name detection, both injection layers, rewrite shapes recognised and
      not, fragment contents, and unknown-image behaviour.

**Commit:** `Extract shared resources from repo compose files`

> **Verified end to end against real docker:** extract → create two workspaces → each gets its own
> database on one container → `up`/`down` refcounting → `rm` drops only its own database.

---

## M5 — The app surface, layer chips, and doctor

**Goal:** make the hidden layer discoverable, and safe to leave running for a month.

- [x] **Set up → Shared** — list + detail between Repos and Stacks: running badge, address, capacity
      meter, slot table, "what it changes" with layer chips, "reaches", and
      start / stop / enable-disable / capacity / reclaim / delete.
- [x] **Extract flow in the app** — pick repo → compose file → service, preview the proposal (published
      values, each override with the reason for its layer, warnings, the lifted fragment), then accept.
      Nothing is written until you do, and the host port is leased only on accept.
- [x] **Typed-name delete** that lists every workspace and database it destroys, with the button disabled
      until the name matches — and which now actually takes the container and volume with it.
- [x] Stale-slot nudge and the at-capacity banner, with Reclaim inline.
- [x] Fixed app-wide: Fluent's default selection was an off-palette purple, invisible until a page
      auto-selected a row. Every list now uses the nav's accent tint.
- [ ] **Layer chips outside this page** — the plan view on workspace detail, and "overridden on this
      machine" markers in the repo and stack editors (the reverse view that makes R1's coupling visible
      from the repo owner's side).
- [ ] Create preview shows the plan diff; "Create without shared resources" button.
- [ ] `doctor`: reclaim slots whose workspace is gone, flag databases with no slot, stop a container with a
      zero refcount, and **re-validate every enabled overlay's targets against current repo configs**.
- [ ] Docs: `config-reference.md` gains a shared-resource section; `user-guide.md` gains a walkthrough.

**Commit:** `Add the Shared page and layer provenance to the app`

> **Verified in the running app** against the real `sprig-example-dotnet` repo: extract postgres:17 →
> create a workspace → slot 1 holds `sprig_pool-test` on one container → capacity, at-capacity banner,
> and the typed delete confirmation. Captures in `captures/`.

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
