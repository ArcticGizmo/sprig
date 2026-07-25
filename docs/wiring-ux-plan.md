# Stack-wiring UX — phased implementation plan

Companion to the design exploration in [`stack-wiring-ux.html`](./stack-wiring-ux.html). That
document argues the wiring stage feels like repeated work because most of it is a mechanical
`input → same-named port` mapping; the only interesting cases are a **shared port** and a
**transform**. This plan turns those ideas into shippable milestones.

## Decisions (locked)

- **Sharing is stored, not inferred.** `StackDefinition` gains an explicit `Shares` list (schema
  bumped to **2**). This is the user's chosen model over "derive from bindings". See Milestone 0.
- **`Bindings` stays the resolution source of truth.** `StackResolver → ResolvedStack → StackWiring`
  is left completely untouched, so the workspace-create path, the CLI, and every existing test keep
  working. `Shares` is an explicit, *validated* overlay that drives the builder UI, rename
  propagation, and the canvas — never the substitution engine. This keeps a model change off the
  hot path.
- **Transforms remain expression-based.** A "transform module" is UI sugar that recognises and
  generates binding expressions (e.g. `http://localhost:${sprig.ports.api_port}`); no schema change.
- **Migration is transparent.** JSON is reflection-based and case-insensitive, so a schema-1 file
  simply deserialises with `Shares = []`; on load it is normalised (shares back-filled from bindings)
  and re-saved as schema 2. No manual migration step for the user.
- **All logic is unit-tested as it lands. Visual verification is deferred** to
  [`visual-followups.md`](./visual-followups.md) (the requester is away; only the canvas and a few
  overlay tweaks need eyeballing).

## Invariant that keeps `Shares` honest

> For every `SharedPort { Port, Consumers[] }`: `Port` is a declared stack port, every consumer's
> `Repo` is in the stack, and that consumer's binding expression references `${sprig.ports.<Port>}`.

Enforced in `StackStore.Validate`, maintained by the builder, and re-established by migration.

---

## Milestone 0 — Foundation: the shared-port model (schema v2)

**Goal:** make sharing first-class data without disturbing resolution.

- `StackDefinition`: add `IReadOnlyList<SharedPort> Shares` (default `[]`); default `Schema` → `2`.
- New records `SharedPort { string Port; IReadOnlyList<PortConsumer> Consumers }` and
  `PortConsumer { string Repo; string Input }`.
- `StackMigration.Normalize(def)`: if `Schema < 2` **or** `Shares` empty, derive shares from
  bindings (any stack port referenced by ≥2 `(repo,input)` consumers) and stamp `Schema = 2`.
  Applied in `StackStore.Get/List/Import`.
- `StackStore.Validate`: enforce the invariant above (clear messages).
- Docs: extend `docs/config-reference.md` stack section with `shares` + a migration note.
- Tests: `StackStoreTests` round-trip with shares; migration back-fill; validation rejects a
  dangling consumer / undeclared port.

**Commit:** `Add explicit shared-port model to stacks (schema v2)`

## Milestone 1 — Auto-wire by convention

**Goal:** remove the mechanical 80%. When repos are chosen, propose the wiring.

- Core `StackAutowire.Propose(repoInputs, existingPorts)` → `{ ports, bindings, shares }`:
  - exact input-name ↔ port-name match binds raw;
  - same input **name across ≥2 repos** ⇒ one proposed shared port + a `SharedPort` entry;
  - a URL-shaped `example` (`^https?://`) ⇒ a `localhost URL` transform over a derived port;
  - honours `AllowedPorts` when choosing/created ports;
  - everything is a *proposal* — never overwrites a binding the user already typed.
- Builder: an **Auto-wire** button that applies the proposal to the current form; per-row `auto` tag.
- Tests: matching, shared-name grouping, URL wrapping, AllowedPorts, idempotence/no-clobber.

**Commit:** `Auto-wire stack bindings by convention`

## Milestone 2 — Collapse the mechanical, surface the exceptional

**Goal:** spend screen space on decisions, not identity mappings.

- Builder VM classifies each row: `Identity | Transform | Shared | Unbound`.
- Clean `Identity` rows fold into one confirmable summary strip; `Transform`, `Shared`, and
  `Unbound` rows stay expanded and labelled.
- Pure view state over the same bindings — no persistence change.
- Tests: classification across the `web+api` fixture; counts; expand/collapse state.

**Commit:** `Collapse auto-wired rows, surface wiring exceptions`

## Milestone 3 — Transform modules

**Goal:** make "derive a URL from a port" a named, pickable action.

- Core `TransformPresets`: `Raw`, `LocalhostUrl` (http/https), `HostPort`. `Generate(preset, port)`
  and best-effort `Recognize(expr) → (preset, port)`; unknown ⇒ `Custom` (raw expression).
- Builder: a small transform picker on a port-referencing row, with the raw `SprigTokenBox` still
  underneath for bespoke expressions.
- Tests: round-trip generate↔recognize for each preset; custom fallback.

**Commit:** `Add transform presets for port-derived bindings`

## Milestone 4 — Explicit "share a port" + rename propagation

**Goal:** turn the shared relationship into a first-class action, backed by the M0 model.

- Builder: "share this port with…" / "stop sharing" actions that add/remove `PortConsumer`s and keep
  the referenced bindings consistent (writes both `Bindings` and `Shares` on save).
- Renaming a stack port propagates to every binding expression and every `SharedPort.Port`.
- Tests: link/unlink updates shares + bindings; rename rewrites all references; save emits a valid
  `Shares` per the invariant.

**Commit:** `Share ports explicitly and propagate port renames`

## Milestone 5 — The patchbay canvas (opt-in view)

**Goal:** the graphical wiring surface — a second view on the same bindings.

- A testable `WiringGraphViewModel` derives nodes (repos + input pins, port rail) and edges
  (bindings, with shared/transform flags) from `repos + Ports + Bindings + Shares`.
- An Avalonia `Canvas`/`Shapes` view renders it: repos with input pins, central port rail, bezier
  cables, shared-port highlight, transform markers. A toggle beside the list switches list ⇄ canvas.
- Logic (graph derivation, hit-classification) is unit-tested now; **all visual correctness is
  logged to `visual-followups.md`** for review on return.

**Commit:** `Add opt-in patchbay canvas for stack wiring`

---

## Sequencing & risk

M0 is the only persistence change and is isolated + fully unit-testable. M1–M4 are additive builder
logic over the M0 model; each is independently revertable. M5 is greenfield UI whose *logic* is
tested but whose *look* is deferred. Every milestone ends green (`dotnet build` + `dotnet test`) and
is committed on the `ux` branch. No pushes, no branch deletion.
