# Checkout, then up

## Why

Asked to "start a worktree" in a real project, an agent following the sprig-workspaces skill ran `sprig up -b <branch>`, which checked the branch out and then built and started the whole stack. What it wanted was a branch in a slot with its ports and files, and nothing running. `--no-start` existed, but a flag that turns off the expensive half of a command is easy to miss, and the skill led with the command that starts everything.

## Decisions (user, 2026-11-17)

1. **`sprig checkout -b <branch>`** checks a branch out in a slot and makes it ready: worktrees, ports, files and the infra's definitions. Nothing starts. It owns everything about placing a branch: `--slot`, `--from`, `--empty-data`, `--fix-slot`, `--unset`, `--set`, `--trust`.
2. **A strict split.** `sprig up` only runs a workspace that's already checked out, named by branch, slot number or folder (`sprig up feat-x`, `sprig up 2`), or the slot this folder is in, else `main`. It never makes a slot. `-b` and `--no-start` are gone from `up`'s help; given anyway, they're refused with the `checkout` line to run instead, so an agent or a habit is pointed the right way.
3. **The skills check out first** and start only what the work needs running (`sprig start <app>` before `sprig up`).

## As built (2026-11-17)

- **CLI**: `CheckoutCommand` and `UpCommand` share `ReadySettings` (the ID and `--set`) and `Readying` (the approve / ask / take-over loop, progress and summary). `UpCommand.Target` resolves the workspace and refuses one that doesn't exist or is free, naming the `checkout` that fills it. Every hint that said `sprig up -b` now says `sprig checkout -b`.
- **The hook** was already a checkout (`CheckOut` with nothing starting), so contract 1 is unchanged.
- **Docs**: `docs/agent-guide.md` (what `sprig docs agent` prints), both skills, the site's quick start, workspaces guide, commands reference and home page, and the app's Home page terminal hints.
- **Tests**: `CheckoutCommandTests` (checkout starts nothing; it needs `-b`; `up -b` and `up --no-start` point at checkout and make nothing; `up` refuses a workspace that isn't checked out and a free slot).
