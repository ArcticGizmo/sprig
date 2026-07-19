# Sprig — Rework: unidirectional data flow (stack-provides-everything)

**Why:** the repo-produces-and-consumes model (`provides`, repo-owned ports) is hard to trace.
Rework so **every value originates in the stack** and flows one-way into repos.

**Model (locked):**
- **Repo** = consumer. Declares `inputs: [{name, example, description?}]`; env/compose templates
  reference only `${sprig.<input>}` and `${sprig.workspace}`. No `ports`, no `provides`.
- **Stack** = producer.
  - **Named ports** (auto-allocated, non-colliding; incrementing preview while authoring).
  - **Per-repo bindings**: `bindings[repo][input] = expression` (literal or template over
    `${sprig.ports.<name>}` / `${sprig.workspace}`). Same-named inputs in different repos are
    **independent** — supplied individually.
- **Resolution at create:** allocate a port per stack port var → for each repo, evaluate each
  input's binding → build that repo's input scope → clobber env / generate compose. Any declared
  input without a binding → **hard-fail** (names repo + input + example).

**Exit re-verification:** `web+api` (frontend URL points at the API's allocated port via a shared
stack port) and `web-only` (frontend URL = literal) both stand up; source repos pristine.

---

## R0 — Config model + validator
- [ ] Repo: drop `Ports`/`Provides`; add `Inputs: [{Name, Example?, Description?}]`.
- [ ] Stack: drop `Vars`; add `Ports: [name…]` and `Bindings: {repo: {input: expr}}`. Keep `Repos`.
- [ ] Validators for both; `ConfigReferences` → `DeclaredInputs`/unbound-input checks.
- [ ] Tests.

## R1 — Resolution + WorkspaceService
- [ ] New `StackWiring`/scope builder: allocate stack ports → per-repo input scope from bindings.
- [ ] Delete `StackScopeBuilder`, `SprigScope` provides path (keep a simple scope helper).
- [ ] `WorkspaceService.Create` uses the new resolver; record stores allocated ports + resolved
      per-repo inputs. Hard-fail on unbound input.
- [ ] Tests (single repo, two repos sharing a port, vue-only literal, missing binding → throw).

## R2 — CLI
- [ ] `repo init`/view reflects inputs. `stack create` gains `--port <name>` (repeatable) and
      `--bind <repo>:<input>=<expr>` (repeatable). `info`/`ls` show resolved inputs.

## R3 — UI
- [ ] Repos config view: show declared **inputs** (name + example) instead of ports/provides.
- [ ] Stacks editor: **ports** section (add named ports, incrementing preview) + **per-repo
      bindings** (each selected repo lists its inputs with example hints + an expression field,
      auto-added). Existing stacks show ports + bindings.
- [ ] Workspaces detail: show resolved per-repo input values.
- [ ] VM tests.

## R4 — Migrate examples + verify
- [ ] Rewrite `sprig-example-vue` (inputs: `apiUrl`) and `sprig-example-dotnet` (inputs: `port`,
      `dbPort`) `.sprig.json`. Recreate `web+api` (shared `api_port`) and `web-only` (literal).
- [ ] End-to-end re-verification; note in `docs/rework-dataflow-verification.md`.

---
Commit per step; local only. Net effect: **smaller core** (no provides / two-phase resolver /
per-repo port namespacing) and a one-directional, greppable data path.
