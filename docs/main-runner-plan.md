# Main as a runner: plan

Branch: `main-runner`. Written 2026-11-13. Status: built the same day, not yet tried by hand. Agreed in outline (user, 2026-11-13: "go for it, I want to see what you come up with"); the open choices were left to Claude. Replaces decision 1's values and settings rules in `main-checkout-plan.md`.

## Why

Main runs the repos where they're checked out, and until now it went through the same `up` as a slot: ports leased, values written into the repo's own `.env`, launch profile and `appsettings`, compose files rewritten onto sprig's ports, and sprig's own copy of the infra started. The user's view: an app that only works once sprig rewrites its config never worked on a fresh checkout, so main has no business changing it. For people running their main checkout, sprig is a runner, not an injector.

The first cut still started sprig's own copy of main's infra. The user keeps their own `docker compose` running for main, so the two clashed (user, 2026-11-13). Main's infra is now theirs too.

## Decisions

1. **Main writes nothing into the repos.** No `.env`, launch profile or `appsettings` values. The first up after this change takes out what earlier ups wrote.
2. **Main has no values and asks for none.** A setting with no default no longer stops main.
3. **Main's ports are the repo's own.** For each port, the first of: `main:` on the port (new key, `http: { host: any, main: 5000 }`); the port's `host` when it names one port; a container's or compose service's own port. An app's port with none of these is unknown on main: its health check is skipped, its quick links are left out, and sprig learns the port from the app's "listening on" line.
4. **An app's known port that's taken stops main, naming it.** Main doesn't move to another port; running elsewhere is what slots are for.
5. **Main's infra is the user's** (user, 2026-11-13). Sprig never starts, stops or empties it. Bringing main up, and starting its apps, check that the infra they use is listening on its port and fail with what isn't (user: "make sure the start operation raises an error if there is missing infra"). Take down and quitting stop main's apps only.
6. **Ordering and the graph still come from the values.** Main's apps get no values, but the plan keeps what they'd have pointed at (`PlannedApp.Uses`), so an app still starts after what it uses, the graph still draws its links, and a start checks the infra it needs.
7. **Main looks different** (user, 2026-11-13: "as long as running main looks and feels different we are ok"). Its own section on the Workspaces page and in `sprig ls`; a MAIN CHECKOUT · YOUR INFRA chip by its title; its infra shown as "yours" by whether it's listening; no Empty data or container Start/Stop.

## As built (2026-11-13)

- **Format**: `main` on a port, in `DefinitionKeys`, the schema (regenerated), the reader and `docs/definition-format.md`'s "In the main checkout".
- **Planner**: `PlanInput.Main`. `AllocateMainPorts` gives each port its main port and refuses an app's that's taken; infra's ports are expected to be in use. Values are resolved, then dropped from the plan, leaving `Uses` (what they reached) for `WorkspaceTopology`. No asks; compose `set:` isn't applied.
- **WorkspaceService**: `OwnInfraOf(plan, needed)` says, from what's listening, whether each container and compose service is there. `Up` for main runs `CheckOwnInfra` (a "Your infra" step) in place of starting infra, after stopping any infra sprig started for main before (its volumes are kept, unreferenced). `MissingInfra` for main throws when what's needed isn't listening, so Start in the app and `sprig start` fail with it. `InfraTargets` is empty for main, `EmptyData` refuses it, and docker isn't needed to bring it up.
- **Runner**: a health check on an unknown port is skipped for main only. An app with no port takes the one its "listening on" line names into its status.
- **App**: MAIN CHECKOUTS and WORKSPACES sections in the overview, sharing one row template. Main's page has the chip, "its own ports, nothing written", "Values in: none", infra rows "yours, on 5432", and tooltips saying Take down leaves its infra running. The graph polls main's infra by port and shows "yours · listening" or "yours · not listening". The headless sample has two mains and renders main's page and graph.
- **CLI**: `sprig ls` has a "Main checkouts (run as they are, with your own infra)" table; `sprig up`'s summary for main says the same and lists infra as "yours, on …".
- **Tests**: planner (ports, no values or asks, order and `Uses`; an app's taken port stops it while infra's is expected); engine (main writes nothing; main won't come up or start without its infra, then does, and refuses Empty data). 492 pass.

## After the user's first use (2026-11-13)

- **Main's links were gone**, since every one needed an app port main doesn't know. On main, a link to an app with no known port now keeps a `{port:owner/app}` token (`HeardPort`) instead of being dropped; the app's row takes the port the app says it listens on (`WorkspaceAppRow.HeardPort`, from the runner's status), and its url, its details' links, its menu's links and the workspace's Links ▾ fill in from that. A link still waiting is left out until the app says.
- **Double-clicking a graph node opens the details** (if folded away) rather than its log; the log is on its right-click menu.
- **The details always start open and aren't kept**: `UiStateData.DetailsClosed` is gone. Folding them lasts until another workspace is opened or the app starts again; a narrow window still folds them only for now.
- **`sprig plan` on main** plans it as main.

## Not done

- The volumes of infra sprig started for main before this change are left in docker; nothing lists them.
- A compose service mapped by its file to a host port other than its container port needs `main:` for sprig to check the right one.
