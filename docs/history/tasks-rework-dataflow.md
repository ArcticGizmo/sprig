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

## R0 — Config model + validator ✅ DONE
- [x] Repo: dropped `Ports`/`Provides`; added `Inputs: [{Name, Example?, Description?}]`.
- [x] Stack: dropped `Vars`; added `Ports: [name…]` and `Bindings: {repo: {input: expr}}`.
- [x] Validators updated; `ConfigReferences.UndeclaredReferences` flags refs that aren't a
      declared input/`workspace` (validator rejects them). Tests updated.

## R1 — Resolution + WorkspaceService ✅ DONE
- [x] New `StackWiring.Resolve`: allocate stack ports → per-repo input scope from bindings.
- [x] Deleted `StackScopeBuilder` + the `SprigScope` provides path (kept a minimal scope helper).
- [x] `WorkspaceService.Create` uses the new resolver; record stores allocated ports + per-repo
      resolved `Inputs`. Unbound input → hard-fail + rollback. Tests cover single/shared/literal/throw.

## R2 — CLI ✅ DONE
- [x] `stack create --port <name> --bind <repo>:<input>=<expr>` (both repeatable). `create` prints
      the stack ports + each repo's resolved inputs.

## R3 — UI ✅ DONE
- [x] Repos config view shows declared **inputs** (name + example, "supplied by the stack").
- [x] Stacks editor: **Ports** section (add named ports, ≈ incrementing preview) + per-repo
      **Inputs** bindings (each repo's inputs auto-listed with example hints + expression field);
      existing stacks show their ports.
- [x] Workspaces detail shows resolved per-repo inputs.

## R4 — Migrate examples + verify ✅ DONE
- [x] Rewrote both example `.sprig.json` to `inputs`. Recreated `web+api` (shared `api_port`
      wires the API's listen port + the web's URL) and `web-only` (literal `apiUrl`).
- [x] End-to-end verified via CLI: web+api → web URL & API port both = 20001 (shared); web-only →
      URL literal, vue-only workspace stands up. **119 tests green.**

---
Commit per step; local only. Net effect: **smaller core** (no provides / two-phase resolver /
per-repo port namespacing) and a one-directional, greppable data path.
