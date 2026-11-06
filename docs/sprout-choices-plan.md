# Sprout choices, repos on their own, and filling needs in code: plan

Branch: `experimental`. Written 2026-11-06. Status: agreed (user, 2026-11-06: "Go ahead with your suggested order and changes"). Breaking changes allowed.

## Why

- Worktrees and port randomisation were set in a trellis's code and asked for when making a trellis, but they're about one running copy, not about which repos run together.
- Running one repo needed a trellis of one repo (the testbed's `services.trellis.cs`): ceremony for the commonest case.
- **Fill it in code** opened the editor and left the user to work out the line.

The split this lands on: a **repo** says what it runs, a **trellis** says which repos run together and how they're wired, and a **sprout** says how this copy runs.

## 1. A sprout's own choices (M) ✅ done 2026-11-06

Checked on a fresh testbed in a throwaway store: an old trellis fails at its `Worktrees`/`Ports` lines with the note; a sprout with no name is refused with what to do; a repo renamed to `master` branched from `master` beside two from `main`; `--random-ports` was recorded and kept when brought up again; asking an existing sprout for in place was refused; an in-place sprout came up again without naming it.

- **SDK:** `trellis.Worktrees()` and `trellis.Ports()` are gone, kept only as compile errors that say where the choice went. `TrellisModel` loses `Worktrees` and `PortPolicy`. Model schema 8.
- **Engine:** `SproutService.Up` takes `SproutOptions(InPlace, From, RandomPorts)`. A new sprout uses them; an existing one keeps what it was made with, and options that differ are refused (switching means a new sprout). Without `From`, each repo's worktree branches from that repo's default branch (`origin/HEAD`, else `main` or `master`, else what its checkout is on), so mixed `main`/`master` repos work. The plan records the sprout's port policy and where its worktrees came from.
- **CLI:** `sprig up <trellis> --sprout x` makes worktrees; `--in-place` (the sprout defaults to `local`), `--from <branch>` and `--random-ports` change that.
- **App:** the new-sprout form asks: own worktrees (branch, start from) or run in place; same ports each time or fresh ones. The last answers are remembered per trellis. The new-trellis form loses both questions, and the trellis Overview's "How sprouts run" keeps only its groups.
- **Testbed:** the trellises drop their `Worktrees`/`Ports` lines.

## 2. A repo on its own (M) ✅ done 2026-11-06

Checked on a fresh testbed in a throwaway store: `sprig repo add shop-services`, then `sprig up shop-services --in-place -d` brought up the repo's compose services and its own Mailpit, and both APIs were ready; an order's email reached that Mailpit; `sprig plan storefront` had only storefront's `mail`, not the repo's; `rm` left no containers or changed files. Built as planned, plus: an entry taken off the Repos list still loads as a lone repo, so its sprouts say "isn't in sprig's Repos list" rather than failing to read; `LoadedTrellis.Alone` and `PlanInput.Alone` carry the fact, so only a lone repo's unfilled import names `OrElse`; the Repos page's Needs says what a fallback uses ("else it uses its own mail").

- **Start a sprout** on the Repos page, and repos in the new-sprout form's picker. `sprig up <repo-id> --sprout x` works too.
- **Engine:** the loader treats a Repos entry's `repo.json` path as a trellis of that one repo, so every place that reloads a sprout's trellis by path works unchanged. The sprout is keyed by the repo's ID, so IDs are one namespace: a trellis can't take a repo's ID or the reverse.
- **SDK:** `repo.Import<Url>("mail-smtp").OrElse(value)` says what to use when nothing else fills the import, typically the repo's own infra (`.OrElse(repo.Mailpit("mail").SmtpUrl)`); a trellis's binding or another repo's export still wins. A repo whose needs aren't all filled can't sprout alone, and says which.
- **Testbed:** `shop-services` fills `mail-smtp` with `OrElse`, and `services.trellis.cs` goes.

## 3. Fill it in code, with the line written for you (M)

- For each unfilled need, offer concrete lines, built from the syntax tree (no semantic model): infra from the presets that offers it (Mailpit for SMTP, Postgres for a connection string, Redis, RabbitMQ), a binding to one repo's export when several offer it (naming the repo's variable, adding one when the repo line has none), or a literal to fill in.
- A dialog shows the change as a diff with **Add to the code**; saving goes through the usual save, so it plans again and the step ticks itself off. Code it can't place a line in opens with the line ready to paste.

## Order

1 before 2, since a lone repo's sprout needs its choices to live somewhere other than a trellis. 3 is independent.
