# Orchestration as links

An orchestration glues separate repos together so they can give each other values. A repo run on its own takes its inputs' defaults. Pointed at another workspace, an input takes that workspace's output instead. You can do this by hand for a one-off ("run my shop-web branch against shop-services on main"); an orchestration is only automation on top, so you don't have to wire the same thing every time.

Today an orchestration's slot is one folder holding all its repos (`storefront--1/shop-web`), on one branch, planned as one unit. This plan replaces that with plain workspaces joined by links.

## Decisions

- **A link lives on the workspace that uses it** (user, 2026-11-23). `shop-web--1` records "inputs from `shop-services--2`". It's matched by name, as orchestrations match today: every input of shop-web that shop-services outputs under the same name takes that output. An input can be pointed at an output of another name (`--input catalog=catalog-url`). A workspace links to one workspace of each repo at a time: linking shop-web to `shop-services--2` after `shop-services main` replaces the link, since nobody means both.
- **Where an input's value comes from, first to last:** a value set on the workspace, a link, the orchestration's binding (old-style slots only), another repo's output in the same slot (old-style slots only), the repo's default. Without any of them it's asked for, as now.
- **The target's outputs are read from its record, not by planning it again** (user, 2026-11-23). A workspace's plan now keeps each repo's outputs, resolved. Its ports are leased and hold still, so its outputs do too. A main checkout that has never come up is planned on the spot; main's ports come from the repo's own config, so that's stable too.
- **Main is a fine target** (user, 2026-11-23), and the most common one. Main itself takes no values, so it can't have links of its own.
- **Branches needn't match** (user, 2026-11-23). An orchestration's checkout may still default every repo to the same branch.
- **Repos can point at each other; values can't loop** (user, 2026-11-23). shop-web's input from shop-services' api url and shop-services' input from shop-web's url (for CORS) is fine: both are ports, leased before any value is filled in. An output that only passes a linked input back through (`value: ${inputs.x}`) and comes round to the workspace that started it is a loop, and an error. Start order is a graph of its own, already built without loops (`Planner.Order`).
- **When the target changes, the user knows** (user, 2026-11-23). After a workspace is made ready, every workspace linked to it that's been made ready before gets its files rewritten if its linked values changed, and sprig says which apps to restart. Nothing restarts on its own.
- **A link to a workspace that's gone stays, and is broken** (user, 2026-11-23). Making the user ready fails with "links to shop-services--2, which isn't there any more", until it's unlinked or pointed elsewhere. It never falls back to the default without saying. A target that's free (released) still has its ports and outputs, so the link works, with a warning.
- **Starting a workspace whose target is down doesn't start the target** (user, 2026-11-23). Sprig says so and offers to start it.
- **No visible "set" of workspaces** (user, 2026-11-23). Each workspace is its own row. One that has links shows 🔗 and what it's linked to; hovering lists each input and where it comes from.
- **The workspaces an orchestration checks out remember it** (user, 2026-11-23). A hidden "made by storefront · feat-x" on each workspace the checkout created, so releasing or stopping that checkout touches only those. A workspace that was already there and only linked in (a shared auth repo's main, an existing slot) is left alone. The set is never worked out from the links: a hub that everything links to would pull every workspace in.
- **Orchestration-wide infra is put off** (user, 2026-11-23). Shared infra comes from a central repo that every other repo links to, which is just a link. `containers:` and `links:` in an orchestration file keep working only for old-style slots until this is settled.

## Pieces

1. **Engine: links.** `PlannedOutput` and `Plan.Outputs`; `WorkspaceLink` and `WorkspaceRecord.Links`; `PlanInput.Linked`, taken after the workspace's own values; `WorkspaceService.Link` and `Unlink`; links resolved from the targets' plans (main planned on the spot), with errors for a target that's gone, a type that doesn't match, two links giving one input, and a loop; workspaces linked to one that's made ready get their files rewritten when their values change. Tests.
2. **CLI.** `sprig link <id> [branch] [--input name=output]` and `sprig unlink [<id> [branch]]`, on this folder's workspace or `--id`/`-b`, making it ready again when it was and saying what to restart; `sprig inputs` says "← shop-services--2"; `sprig workspaces` shows 🔗; `--json` throughout; docs.
3. **Orchestration checkout.** `sprig checkout <orchestration> -b feat-x` checks out a slot of each repo's own (`shop-web--1`, `shop-services--2`), links them as the file says, and gives them its literal input values. A repo can be marked to use its main checkout rather than a slot (`auth: { use: auth, checkout: main }`). Every repo is made ready first, then the links are filled in, so two repos that link to each other both find ports to point at. Each workspace it made is marked with it; `sprig up/stop/free <orchestration> feat-x` act on those. An orchestration's groups start the named apps across them. No new old-style slots are made; the ones there keep running until freed.
4. **App.** 🔗 on rows, the hover with each input's source, a broken link in red, **Link to…** in the ⋯ menu opening a dialog to pick a workspace (with search) and see what it would fill, the restart offer after a target changes, and the New workspace form checking out an orchestration as linked slots.
5. **Graph.** A linked workspace's providers drawn as tags on the nodes that use them, as infra is, with start/stop for the target from there.

## Open

- Orchestration-wide `containers:` and `links:` (put off; see above).
- `sprig cd storefront 2 shop-services/api` goes away with old-style slots; whether an orchestration's checkout gets a cd of its own (`sprig cd storefront feat-x shop-services`) or each workspace is reached alone.
