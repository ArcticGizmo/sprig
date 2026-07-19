# Sprig — Task Breakdown: M7 (Polish & packaging — shippable)

**Milestone goal:** take the working M1–M6 product to *shippable*: real user-facing docs, a
`doctor` surface in the UI, an error-message + empty-state pass, and a packaged Windows build.

**Exit criteria:**
1. A newcomer can install/run sprig and configure a repo + stack + workspace using the docs alone.
2. Docs reflect the **current** model (stack-provides-everything: repo `inputs`, stack `ports` +
   per-repo `bindings`) — no stale `provides`/`vars` language anywhere.
3. `doctor`/reconcile is reachable and legible from the UI.
4. A packaged build installs and runs the full flow on a clean Windows machine, and **notifies**
   the user when a newer version exists (no auto-apply, no code signing this milestone).
5. Solution builds; existing Core + VM tests stay green.

**Chosen sequencing (locked with user):** docs + polish first; packaging last.

---

## M7.1 — Docs (README + user guide + config reference) ✅ DONE
- [x] **M7.1.1** `README.md`: what sprig is, the worktree+infra isolation model, quick-start
      (register a repo → define a stack → create a workspace → up/down/teardown), where state lives
      (`%LOCALAPPDATA%\sprig`), and a link to the deeper docs.
- [x] **M7.1.2** `docs/config-reference.md`: the `.sprig.json` schema — **repo = consumer**
      (`inputs: [{name, example, description?}]`; env/compose templates over `${sprig.<input>}` and
      `${sprig.workspace}`) and **stack = producer** (named `ports`, per-repo `bindings[repo][input]`
      with `${sprig.ports.<name>}` / `${sprig.workspace}`). Worked `web+api` + `web-only` examples
      (transcribed from the real example repos + stored stacks, not invented).
- [x] **M7.1.3** `docs/user-guide.md`: end-to-end walkthrough for both the **UI** and the **CLI**
      (`repo`, `stack`, `create`, `up/down/reset`, `remove`, `reconcile`/`doctor`, `init`), incl.
      drift/reconcile state table and teardown/force semantics.
- [x] **M7.1.4** Purged stale model language: fixed the two `provides` doc-comments in Core
      (`ConfigReferences.cs`, `IVariableSource.cs`). Historical milestone docs left as-is (a record).

## M7.2 — Error-message + empty-state pass
- [ ] **M7.2.1** Audit user-facing exception/error strings across Core + App for a clean-machine
      user (no docker, no git, unregistered repo, unbound input, name collision) — make each
      actionable. Verify the unbound-input failure names repo + input + example.
- [ ] **M7.2.2** UI empty/edge states: no repos, no stacks, no workspaces, docker-unavailable,
      a workspace with **no infra** (Up/Down/Reset already hidden — confirm the detail reads well).

## M7.3 — Doctor / reconcile UX in the UI
- [ ] **M7.3.1** Surface a legible drift/health view: per-repo worktree state
      (Healthy/MissingFolder/Orphaned/Gone) with plain-language labels; Reconcile (diagnose) and
      Repair (fix) clearly distinguished. (Commands already exist in `WorkspacesViewModel`.)
- [ ] **M7.3.2** Optional: a top-level "Doctor" affordance mirroring the CLI `doctor` (docker/compose
      availability + worktree integrity across all instances). Keep logic in Core.

## M7.4 — Packaging (Velopack, update-notify only)  ⬅ last
- [ ] **M7.4.1** Add Velopack to `Sprig.App`; produce an installable Windows build that runs the
      full flow on a clean machine. **No code signing** this milestone.
- [ ] **M7.4.2** Update **notification** only: on launch, check the release feed and, if a newer
      version exists, surface a non-blocking "update available" notice. **Do not** download/apply
      automatically. (Feed source TBD — GitHub releases or a local/file feed for now.)
- [ ] **M7.4.3** App identity polish for the installer: app id, product name, icon, version stamp.

---

## Open questions
- **Update feed source** for M7.4.2: GitHub releases vs. a file/UNC feed? (Affects the check URL
  only — the notify UX is the same.) Deferred until we reach M7.4.
- **Icon/branding** assets for M7.4.3 — reuse the in-app palette/mark or supply a dedicated icon.

## Notes
- Docs must describe the **current** unidirectional model (see `tasks-rework-dataflow.md`), not the
  original `provides`/`vars` design in `implementation-plan.md` §3.1.
- Commit per sub-milestone; local only.
- Keep all logic in `Sprig.Core`; packaging/update-check lives in `Sprig.App` but stays thin.
