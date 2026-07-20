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

## M7.2 — Error-message + empty-state pass ✅ DONE
- [x] **M7.2.1** Audited user-facing error strings across Core + App. Most were already actionable
      (unbound-input names repo + input + example ✓; unknown repo/stack/workspace, name collision,
      "not a git repository", "register it first" all clear). Improved the docker-unavailable
      message to "docker compose is not available — is Docker Desktop installed and running?".
- [x] **M7.2.2** UI empty/edge states: added first-run empty hints for **no repos**, **no stacks**,
      and **no workspaces** (each points at the relevant add/new action, and stale "use the CLI"
      copy replaced). Docker-unavailable surfaces via the improved Core message + error text. A
      workspace with **no infra** already hides Up/Down/Reset — confirmed via headless render
      (`example-web-only` shows only Reconcile/Repair/Open/Remove).

## M7.3 — Doctor / reconcile UX in the UI ✅ DONE (essentials)
- [x] **M7.3.1** Legible drift/health view: per-repo worktree state now shows a plain-language
      label instead of the raw enum — `✓ in sync` (green) / "worktree folder missing — run Repair" /
      "orphaned folder (git lost track) — run Repair" / "gone" (red for problems). Overall drift
      line reads "in sync" / "drift detected — run Repair" / "worktrees gone" / "not checked".
      Reconcile and Repair carry tooltips spelling out diagnose (read-only) vs fix.
- [ ] **M7.3.2** *(deferred, optional)* Top-level "Doctor" over all workspaces. The per-workspace
      surface now distinguishes diagnose vs fix clearly; a global sweep can come later if wanted.
      The CLI `doctor` (reconcile-all) already covers this need for now.

## M7.4 — Packaging (Velopack, update-notify only) ✅ DONE
- [x] **M7.4.1** Added Velopack 1.2.0 + `VelopackApp.Build().Run()` as the first call in
      `Program.Main` (vpk verified the hook). Published self-contained win-x64 and packed with
      `vpk` → `Setup.exe` + portable zip + release feed. **Verified end-to-end**: silent install to
      `%LocalAppData%\Sprig`, ran the installed build headlessly (`render`), then cleanly
      uninstalled. **No code signing** (vpk warns; deferred). See `docs/packaging.md`.
- [x] **M7.4.2** Notify-only update check (`Updates/UpdateChecker.cs`): on launch checks the feed
      from `SPRIG_UPDATE_FEED`; a newer release shows a dismissible top bar. No-op when the var is
      unset or the app isn't Velopack-installed; failures swallowed so a flaky feed can't block
      launch. **Never downloads/applies.** Verified via the `check-update` probe: an installed
      v0.1.0 against a feed containing v0.2.0 prints "Update available: v0.2.0 — you have v0.1.0".
- [x] **M7.4.3** App identity: `Company`/`Description`, generated multi-size `Assets/sprig.ico`
      (sprout motif, accent on dark), wired as `<ApplicationIcon>` + the window `Icon`.

---

## M7 complete ✅
Docs (README + config-reference + user-guide + packaging), an error-message + empty-state pass,
a legible drift/reconcile UX, and a Velopack-packaged Windows build with notify-only updates.
**119 tests green throughout; verified via headless render + a real install/uninstall cycle.**
Deferred (optional/next): code signing, a hosted update feed, applying updates, and a top-level
"Doctor" over all workspaces.

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
