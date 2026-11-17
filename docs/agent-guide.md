# Sprig for agents

This is what `sprig docs agent` prints: how an agent makes a repo ready for sprig and runs its workspaces. It matches the sprig it ships with. The definition format is `sprig docs format`; the JSON Schema is `sprig schema`.

## Ground rules

- **Pass `--json` to every command whose output you read.** It's a stable contract. Without a terminal, sprig never prompts: anything it would have asked is a flag, and a refusal says which.
- **A failure is exit code 1 and `{ "ok": false, "code": …, "error": … }`.** Branch on `code`:

| `code` | What happened | What to do |
|---|---|---|
| `inputs-needed` | Inputs have no value; `asks` lists each `{ name, type, owner }` | Pass `--set name=value` for each, or ask the user for secrets |
| `untrusted` | A definition isn't approved to run; `untrusted` lists the files | Approve one you wrote with `sprig trust add <file>`. Ask the user before approving one you didn't |
| `held` | Another definition's main is running in this checkout | Ask the user, then pass `--take-over` |
| `slot-broken` | Folders that aren't worktrees are in the slot's way | Say so; `--fix-slot` deletes them, `--slot <n>` uses another slot |
| `slot-busy` | Another sprig (another agent, say) is checking out that slot or branch right now | Wait for it, or leave `--slot` out to use another slot |
| `start-failed` | Apps didn't become ready; `failed` lists each with `Why`, `ReadyWhen` (the check it had to pass) and `LastLines` of its log | Read those, fix the definition or the app, and start again |
| `start-timed-out` | Apps were still starting when sprig stopped waiting (five minutes); `failed` as above | Look at `LastLines`: a slow first build, or a health check that never answers |
| `problems` | The definitions don't plan; `problems` lists each with file and line | Fix them (see below) |
| `error` | Anything else; `error` says what | Read it |

- **Values reach apps only through files** (`.env`, `launchSettings.json`, `appsettings.json`), never environment variables. So you can stop one app and run it yourself (`dotnet run`, `pnpm dev`) in its folder, and it gets exactly what sprig's run gets.
- **Don't remove what you didn't make.** `sprig rm` deletes a workspace's data and worktrees. Use `sprig free` to give a slot back.

## Making a repo ready

1. `sprig init --in-repo` in the repo drafts a `sprig.yaml` from its projects (`.csproj`, `package.json`, compose files). Read it; it's a starting point.
2. Fill in what the draft couldn't know: which apps wait for which infra (`wait-for`), how each app learns its port and connection strings (`files:`), health checks, jobs such as migrations (`once: true`). Read the repo's own config to find where each app reads its port and connection strings; that file is the one to list under `files:`.
3. `sprig check --json` reads and plans the file without running anything or needing approval. Fix every problem it gives, by file and line, and run it again until `ok` is true.
4. `sprig plan --json` shows what a workspace would get: every port, every value written into every file, and where each came from. Check that each app gets the port and addresses it needs.
5. Try it in a slot, not the user's checkout: `sprig up -b <branch> --json --trust`, then `sprig info --json`. `--trust` approves definitions without asking, so pass it only for a definition you wrote.
6. An app that doesn't become ready: the `failed` list in the JSON says why, what it had to do to be ready, and how its log ends; `sprig logs <app> -n 200` for more. Fix the definition (or the app's config), then `sprig start --json` again; a changed definition makes the workspace ready again first.
7. When it runs, `sprig free` gives the slot back. Leave committing the `sprig.yaml` to the user.

Keep the definition plain: every key in it is checked, unknown keys are errors, values are read exactly as written, and `${…}` goes only in values, never in commands.

## Running workspaces

| To | Run |
|---|---|
| See what this folder is to sprig | `sprig info --json` |
| List workspaces | `sprig workspaces --json` |
| Check a branch out in a slot and start it | `sprig up -b <branch> --json` (`--no-start` to only make it ready, `--group <name>` for a slice) |
| Start, and wait until ready | `sprig start [app…] --json` |
| Restart after a code change | `sprig restart --json` (`--jobs` to run migrations again) |
| Stop | `sprig stop [app…]` (nothing named: everything; data is kept) |
| Read an app's log | `sprig logs <app> -n 200` |
| Find a folder | `sprig path [place]`, e.g. `sprig path api` for a folder the repo names under `cd:` |
| Give an input a value | `sprig set name=value` |
| Give the slot back | `sprig free` |
| Give back the slots agents' worktrees left taken | `sprig free --agent-made` (`--running` to stop and free busy ones too) |

Commands run inside a slot's folder act on that slot; elsewhere, name the repo and workspace: `sprig start shop-services feat-x`.

To work on one app yourself, stop just it (`sprig stop catalog-api`), run it from its folder, and leave the rest running; it gets the same port.
