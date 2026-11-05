# Trellis page redesign: plan

Branch: `experimental`. Written 2026-11-05. Status: agreed direction (user, 2026-11-05: the recommended blend from the options canvas). Breaking changes allowed: no migration for old definitions or store data. Phases 1 and 2 done 2026-11-05 (278 tests pass); phase 3 is next.

The options and the reasoning are on the canvas at https://claude.ai/artifact/QqNHSoEZhiQ5B5zjwgBqhy, with a copy in [artefacts/trellis-options/](artefacts/trellis-options/). This plan builds the blend: names and badges from the Model board, a Repos page (C), a summary trellis page (A) with the picture at its centre (B), and the checklist (D) only while something blocks a first sprout.

## Why

Today's Trellises page is `sprig plan` printed in full. It answers "what will every app get?" before "what is this, can it run, how do I start one?". It also mixes two lists in one column, shows where a file is kept only as buttons (Edit, Copy into sprig, Use its own sprig.cs), and has no way to start a sprout.

## Decisions

1. **Three nouns and one badge.** Repo, Trellis and Sprout stay. "Virtual" goes from the app. Every repo and trellis shows where its file is kept: **in repo** or **in sprig**. The app says **needs** and **offers**; the SDK keeps `Import`, `Export` and `Bind`.
2. **Two ways to put a repo in a trellis, not three.** `trellis.Repo("path")` uses the folder's own `sprig.cs`. `trellis.UseRepo("id")` uses an entry from the Repos list, whose file is in the repo or in sprig. `Repo(path, definition: "id")` is removed: a definition in sprig always belongs to a Repos entry.
3. **Where a repo's file is kept is a switch on the repo**, not buttons on a trellis:
   - *In repo → In sprig* copies the folder's `sprig.cs` into sprig, or reuses the copy sprig already has from an earlier switch, or drafts one from the folder if it has neither.
   - *In sprig → In repo* uses the folder's `sprig.cs` if there is one (sprig's copy is kept, so switching back restores it). If the folder has none, sprig's copy is written there as `sprig.cs`, left uncommitted.
   - Every trellis that uses the entry follows the switch; nothing in the trellises changes.
4. **A trellis's file location is shown, not switched.** Moving a trellis between a folder and sprig needs a destination folder; that waits until it's asked for.
5. **The plan says where values come from as data.** `PlannedValue` gains `Sources` beside the `Via` text, so the picture never parses display strings.
6. **No migration.** Model schema goes up, so definitions cached under the old schema are re-run. Repos entries and trellises in the dev store that use `definition:` are deleted rather than converted; the testbed is updated to match.

## Phases

Each phase leaves the app building and the tests passing. Sizes: S is under a day, M is one to two days, L is more.

### 1. Engine: where each value comes from, as data (S) ✅ done 2026-11-05

- [x] `ValueSource(Owner, Resource, Endpoint, Import)` on `PlannedValue.Sources`: each endpoint a value reaches, with the import it crossed through. A trellis binding to a literal has the trellis as owner and no resource. An import nothing fills has no owner.
- [x] Thread sources through `Resolve`, `ResolveRef`, `ResolveImport`, `ResolveNamedExport` and `ResolveTemplate` alongside `via`.
- [x] `Plan.Wires()`: one `PlannedWire` per app value that crosses from another owner or through an import (consumer app and key, import, provider owner, resource and endpoint; no provider when unfilled).
- [x] `PlanView.Wiring` reads wires instead of parsing `Via[0]`, and now names the app a value comes from ("shop-services / catalog-api (catalog-url)").
- [x] Tests: automatic import with a same-repo reference beside it (no wire), trellis bindings to infra, to a literal and to a named export, and an unfilled import.

### 2. Model: one way per repo, and the storage switch (M) ✅ done 2026-11-05

- [x] SDK: `definition:` dropped from `Trellis.Repo` and `RepoRefModel.Definition`; model schema 7, so older cached models re-run.
- [x] Loader: the `definition:` branch is gone. A `Repo(path)` folder without a `sprig.cs` says to commit one or add the folder on the Repos page and use `trellis.UseRepo("id")`.
- [x] `VirtualDefinitions` became `StoredDefinitions` (the Repos list and trellises kept by sprig). `RepoEntry` is `Id`, `Folder` (now required), `InRepo`, `DefinitionFile` and `HasCopy`; sprig's copy lives at `<store>/repos/<id>/sprig.cs` and stays when a repo switches back to the repo. Entries without a folder are ignored.
- [x] `TrellisAuthoring`: `AddRepo`, `KeepInSprig`, `PutInRepo` (decision 3) and `UseFromRepos` (rewrites `Repo("path")` as `UseRepo("id")` through `DefinitionEditor.RepoToUseRepo`). A copy keeps the approval of what it was copied from. `DescribeInSprig`, `UseOwnDefinition`, `WriteRepo`, `ReferenceRepo`, `SetRepoDefinition` and `Scaffold`'s `definition:` output are gone.
- [x] `UsedBy(id)` stays a text search of registered trellises for `UseRepo("id")`; reading models instead waits until the Repos page needs it.
- [x] Tests: each switch direction (with and without the folder's own file, and restoring an earlier copy), `AddRepo` both ways through `CreateTrellis`, `UseFromRepos`, and the Repos-list sprout tests moved to `UseRepo`.
- [x] Interim UI so the app keeps working until phases 3 and 4: the Repos column has one "+ Add"; a trellis's repo rows offer "Keep in sprig" / "Put in the repo" (Repos entries) or "Add to Repos" (repos named by folder); the selected repo has the same switch.
- [x] Data: the dev store had no `repos/` or `trellises/` folders and both registered testbed trellises use `Repo(path)`, so there was nothing to purge.

### 3. Nav and the Repos page (M)

- [ ] Nav: DEFINE holds **Repos** then **Trellises**, each with a one-line subtitle ("what each repo runs", "repos run together"); RUN's Sprouts gets "running copies".
- [ ] Extract the editor pane from `TrellisesViewModel` into a `DefinitionEditorViewModel` (open, save-approves-and-rechecks, discard guard, status line) that both pages host.
- [ ] `ReposViewModel` and `ReposView`: the list (name, badge, "3 apps · 3 infra"), "+ Add" (one modal: folder and ID), and the selected repo: folder (the only place a path shows), "Used by" links, the In repo / In sprig switch with what it means, then Runs, Infra, Offers and Needs from its evaluated model. Open definition, Remove.
- [ ] A repo that isn't approved, fails, or describes a trellis says so where the sections would be.
- [ ] Tests: the list, the switch calling authoring and re-reading, Used by, and each failure state.

### 4. The trellis page: summary first (M)

- [ ] The Trellises column is one list: name, badge, "3 repos · 1 sprout up". "+ Add" opens one modal: new in sprig (name and repos, as now) or an existing `.trellis.cs`.
- [ ] Header: name, badge, a one-line description ("Runs shop-services, shop-web and shop-mobile together"), **Start a sprout** (opens the Sprouts page's new-sprout modal with this trellis chosen), Edit code, and a "⋯" menu for Plan again and Remove.
- [ ] Status line: ready, needs approval, or N problems, with when it was last checked.
- [ ] Tabs: **Overview**, **Values**, **Ports**, **Code**.
- [ ] Overview, until phase 5 puts the picture there: repo cards (badge, runs, infra, offers, needs with what fills each), How sprouts run (worktrees, ports, groups in words), Added by the trellis, and Sprouts of it (from `HostStatus` and the sprout store).
- [ ] A `Repo(path)` repo's card offers "Add to Repos" (`UseFromRepos`); a `UseRepo` card links to the Repos page.
- [ ] Tests: the description, status for each state, card contents from a plan, and Start a sprout choosing the trellis.

### 5. The picture (L)

- [ ] `TrellisGraph` (view model): a box per repo holding its apps, jobs and infra; a dashed box for what the trellis adds; a wire per `PlannedWire`, labelled with its import; a dangling wire for an unfilled need.
- [ ] Layout: repos in columns by how far they are from what they need (providers left, consumers right), the trellis's box under the column it feeds; rows ordered to reduce crossings. Repos with many apps fold to "and N more".
- [ ] `TrellisGraphControl`: draws the boxes and curved wires, scales to fit, highlights a selected app's wires.
- [ ] Clicking an app opens an inspector beside the picture: kind, folder, port, starts after, and its values with where each came from. Clicking a repo selects its card's details.
- [ ] Group chips (Everything, then each group) dim what a group doesn't start.
- [ ] Tests: graph building from the testbed plan, layering, the unfilled-need wire, and group dimming. A headless render of `storefront`.

### 6. Values, Ports and Code tabs (S)

- [ ] Values: today's per-app panels, containers, compose changes and groups, with a filter by repo or app.
- [ ] Ports: today's ports table, grouped by owner, with the trellis's own ports labelled as such.
- [ ] Code: the trellis's file in the shared editor, with the status line saying whether it plans after a save; the tabs stay, so Overview shows the result.

### 7. The checklist, only while something blocks (S)

- [ ] When a trellis can't make a sprout yet, the Overview opens on the steps: Repos, What the repos need, How sprouts run, Approve the code, Start a sprout. Each shows its state and the one action that clears it (Add a Mailpit to fill it, Fill it in code, Read and approve).
- [ ] Start a sprout is disabled with the reason while any step is open. Once all are done the checklist goes and the summary shows.
- [ ] Tests: each step's state from load results and plans.

### 8. Names, testbed, data and docs (S)

- [ ] Sweep the app's text for "virtual", "written in sprig", "referenced", "binding" and "wiring"; use in repo / in sprig, needs and offers.
- [ ] Testbed: trellises stay on `Repo(path)`; add one that uses `UseRepo` so both forms are covered. `HeadlessRenderer` gets `render-repos` and an updated `render-trellises`.
- [x] Delete the dev store's `repos/` and `trellises/` folders and their registrations (decision 6), after listing what's there. Checked in phase 2: nothing to delete.
- [ ] Update the hand-test checklist, the roadmap's M7 section and the changelog's Unreleased notes.

## Order

```mermaid
flowchart LR
  P1["1 Value sources"] --> P5["5 Picture"]
  P2["2 One way per repo"] --> P3["3 Repos page"]
  P2 --> P4["4 Summary page"]
  P3 --> P4
  P4 --> P5
  P4 --> P6["6 Tabs"]
  P4 --> P7["7 Checklist"]
  P5 & P6 & P7 --> P8["8 Names, testbed, docs"]
```

Phases 1 and 2 are independent. Phase 5 is the one to review closely: it's the only new kind of control.

## Risks

- **Layout of the picture.** Twenty apps across four repos is a lot of boxes. Folding apps inside their repo box and drawing wires between repos (expanded on click) is the fallback if it gets busy.
- **Dropping `definition:`** breaks any trellis file that uses it, including ones in the dev store. Decision 6 accepts that; the loader's message says what to use instead.
- **Two pages for authoring.** Moving between Repos and Trellises while editing could feel slower. The trellis cards link straight to their repo, and the repo's Used by links back.

## Not in this plan

- Moving a trellis between a folder and sprig (decision 4).
- Live state on the picture (running, failed) and opening a log from it. That's the roadmap's Live graph item, and it builds on phase 5.
