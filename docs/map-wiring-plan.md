# Map wiring view — plan

**Status:** 2026-11-02 · in progress on `map-needs-visibility` · mock-up: [docs/artefacts/map-wiring.html](artefacts/map-wiring.html)

## Goal

A map's missing pieces only surface today when a checkout fails with `unmet needs`. The Maps section should show, before any checkout, how every module's needs are wired: what is wired, what has nothing behind it, what is ambiguous, what is a constant, which services are shared hubs, and which env values are pinned instead of wired. It should offer the fixes in place, and the CLI should give the same answer.

## Decisions

- **Viewing never clones or throws.** `MapResolver.Resolve` bootstraps unregistered repos and throws on invalid configs. The inspection uses its own loader that reports those as repo problems on the map instead.
- **One source of truth for wiring.** The inspection builds on `MapGraphProjection`, which mirrors `CapabilityResolver`. Any change to the wiring rules lands in both and is covered by tests that run them side by side.
- **Ambiguity becomes resolvable.** A wiring value may name the providing repo: `wiring[repo][capability] = "provider-repo/capability"`. A bare capability keeps today's meaning. Repo names and capability names can't contain `/`, so the forms can't collide.
- **Layout in the app uses MSAGL** (`Microsoft.Msagl` 1.1.6, MIT): layered Sugiyama layout with repo clusters. It stays in `Sprig.App`; Core and the CLI don't take the dependency.
- **Examples stay anonymous** in docs, tests and renders.

## Checkpoints

Each checkpoint ends green (`dotnet test`) and committed.

1. **Inspection model (Core).** `MapInspector` loads a map's repos without side effects and returns a `MapInspection`:
   - every need's status (wired, ambiguous, gap, map default), with the default's values;
   - each provide's consumers, its own-module uses (env, compose, shapes), and whether it is a constant (shapes only, no references);
   - hub counts (distinct modules relying on a module);
   - near-miss suggestions for gaps (edit distance against every provide);
   - value hints for gaps, read from env templates by matching the template around the `${sprig.…}` reference;
   - fixed values: env `set` literals that pin `localhost`/`127.0.0.1` addresses or `*PORT*` numbers;
   - repo problems (not registered, config missing or invalid).
2. **Repo-qualified wiring (Core).** `provider-repo/capability` in `CapabilityResolver`, `MapGraphProjection` and `MapStore` validation; config reference doc updated.
3. **Map edits (Core + App fix).** `MapEditor` for set/remove default and set/remove wiring, with validation. Converting a fixed value: add the need to the module, point the env key at `${sprig.<cap>.<output>}`, and set the map default. Fix `MapsViewModel.SaveMap`, which currently drops a map's wiring, defaults and pool size on edit.
4. **CLI.** `sprig map show` prints the inspection; `sprig map check <name>` exits 1 on gaps, ambiguity or repo problems; `sprig map default` and `sprig map wire` apply the same edits as the app. All with `--json`.
5. **Layout (App).** `MapGraphLayout` turns an inspection into node, cluster, edge and label geometry via MSAGL. Tests: consumers sit above providers, nothing overlaps, every edge has a route.
6. **Wiring canvas (App).** A control that draws the layout: module nodes with provides (◆ for constants), hub badge, repo clusters, gap and default placeholders, coloured edges with labels, selection that dims everything unrelated, pan and fit-to-width.
7. **Maps page.** Health banner, Graph and Ledger tabs, inspector with the fixes (supply a value with its hint, wire to a similar name, pick a provider, edit or remove a constant, convert a fixed value), fixed-values card. Headless render of the page for review.
8. **Docs and release notes.** Config reference, user guide Maps section, CLI help, changelog. Full test run.
