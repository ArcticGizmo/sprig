# Next steps checklist

**Started:** 2026-10-30 · after v0.7.5. Ordered by priority; 1 and 2 together are the proposed v0.8.0.

## 1. Claude Code plugin onto the map model

The plugin (`plugins/sprig`) still drives stacks, which v0.7.0 removed, so its commands fail today.

- [x] `sprig-configure` skill: author `provides`/`needs` per module, virtual repos and maps instead of `inputs` and stacks
- [x] `references/config-cheatsheet.md`: rewrite for schema 1 repo config and maps
- [x] `sprig-workspace` skill: `pool checkout`/`status`, `ws create --map/--repo`, `ws restart`, `ws refresh`, `ws switch`
- [x] Agent primitives in `sprig-workspace`: `sprig here --json`, labels/notes, tags for ownership, `ws urls`
- [x] Claude Code worktree layout (`--worktree-layout claude`) documented where it applies
- [x] `sprig-teardown` skill: pool release vs `ws down` vs `ws rm`, junction drift, tag-based cleanup
- [x] Slash commands: `sprig-create` renamed `sprig-checkout`, new `sprig-release`; `up`/`status`/`down`/`rm`/`doctor` updated
- [x] Plugin `README.md`, plugin version 0.2.0, marketplace description
- [x] Mark `docs/claude-plugin-plan.md` as shipped and superseded
- [ ] Smoke test: `claude --plugin-dir ./plugins/sprig` against the examples sandbox (needs a person)

## 2. Deprecated CLI verbs and stale text

- [x] Drop the `ws reset` alias (promised for one release in v0.7.0); the app's infra "Reset" became "Restart" and `WorkspaceService.Reset` went with it
- [x] `ws create`: un-deprecated (decided 2026-10-30) — it's the tool for single-repo, virtual-repo and partial (`--only`/`--without`) workspaces outside a pool
- [x] Fix stale CLI text: empty-list hints in `ws ls`, `cd` and the workspace picker, `init`'s "next:" hint, `ws info`'s partial line, `init --map` help
- [ ] `InstanceRecord.Stack` and `InstanceRepo.Inputs` are stack leftovers; remove if nothing reads them
- [ ] Update docs, help text and tests that still mention them
- [ ] Changelog entry, then `bump-version` to v0.8.0

## 3. Map canvas and trace view (Graph Turn workstream E)

- [ ] Confirm whether workstreams B/C are done: a valid `${sprig.<need>.<out>}` is not flagged red, and new provide outputs autocomplete live
- [ ] Graph projection in Core (nodes, edges, ambiguity and gap flags), unit-tested
- [ ] Canvas on the Maps page: repos expandable to modules, edges for derived wiring
- [ ] Resolve ambiguities on the canvas (writes `map.wiring`)
- [ ] Supply defaults for gaps on the canvas (writes `map.defaults`)
- [ ] Trace view: click a resolved value to highlight its provider
- [ ] Visual sign-off (user-driven)

## 4. Real Docker lifecycle check

- [ ] Script or manual checklist run against real Docker using `scripts/sandbox`
- [ ] `keep` preserves volumes across re-checkout
- [ ] `fresh` wipes volumes and reinstalls deps
- [ ] Default checkout branches from `upstream/main` when an upstream exists
- [ ] Hook it into the `release-next-version` skill or document it as a pre-release step

## 5. Code signing

- [ ] Decide on a certificate (or Azure Trusted Signing)
- [ ] Add signing to `vpk pack` and `sprig.exe` in the Release workflow
- [ ] Update `docs/packaging.md`

## 6. Stale docs

- [x] Move `docs/visual-followups.md` (stack builder only) to `docs/history/`
- [x] `docs/user-guide.md` and `config-reference.md` used bare `sprig up` / `rm` / `ls` / `create` / `reconcile`, which only exist under `sprig ws`
- [x] `README.md`: CLI quick start used `sprig stack create` and `--stack`; app quick start pointed at a Stacks page; model and state-table text described stacks
- [x] Root `cli.md` (the Graph Turn dev runbook) moved to `docs/history/graph-turn-dev-cli.md`
- [x] Fix status headers: `claude-worktree-layout-plan.md`, `claude-plugin-plan.md`, `cli-review.md`, `graph-turn-followup-plan.md`; `docs/history/README.md` lists the archived files
- [ ] `pool-detached-model-status.md`: remaining items reflect `ws switch` covering "reattach to an existing branch"

## Later (deferred designs)

- [ ] Shared providers with per-workspace namespaces (`orchestration-primitives-plan.md`)
- [ ] Machine-wide running budget and idle infra reclaim
- [ ] `sprig ws watch --json` event stream
- [ ] Per-repo start point on checkout
- [ ] Branch graph across every repo in a workspace, not just the first
