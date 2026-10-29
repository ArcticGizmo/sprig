# Orchestration primitives: purpose, tags, in-place switching and discovery

**Status:** In progress on `orchestration-primitives` · step 1 done (live branch, label and note, purpose-led `ws ls` and app cards, `sprig here`, `pool checkout --detached`, `cd` window titles under Windows Terminal); step 2 done (tags, `ws ls --tag`, `ReleasedTags`); step 3 (`ws switch`) next. Builds on the Graph Turn (maps, `provides`/`needs`) and the parked-workspace model in `docs/pool-detached-model-plan.md`, and revisits that plan's choice of the branch as a workspace's identity.

## Why this exists

Sprig isolates. It should stay process-agnostic: it knows nothing about tickets, PRs, agents or CI. But the tools that drive it (an agent, a CI job, a script, a session monitor such as perch, a developer in a terminal) keep needing the same few things that Sprig cannot supply today:

- **What is each workspace doing?** A worktree is not a branch. `sprig ws ls`, GitHub and statuslines all show branch names, which say little about the work and go stale when someone switches branch inside a worktree.
- **Which workspaces belong to me?** A caller that claims workspaces has no way to mark them as its own and find them again, so it cannot clean up after itself.
- **How do I get going again after a merge?** Once a change is merged or queued, the workspace should move straight onto fresh work without waiting for the old branch to be tidied up.
- **Where am I, and where do I go?** From inside a worktree there is no single call that names the workspace, its ports and its URLs.

## The rule

Every primitive must:

1. **Store and show, never interpret.** Sprig may record information a caller gives it and display or filter on it, but never act on its meaning.
2. **Keep the coupling one-way.** A caller knows about Sprig; Sprig never knows about a caller and never calls out.
3. **Touch nothing in the product.** Values reach apps through files in the worktree (`.env`) or generated files in the central store. A plain `dotnet run` or `npm run dev` in a worktree, or opening it in VS Code, must still work.
4. **Work from a terminal.** Anything a caller can do through `--json --ni`, a person can do interactively.

## 1. Identity in three layers

`InstanceRecord` today treats the claim branch as the load-bearing identity and the label as optional free text. That conflates three things that change at different rates:

| Layer | Example | Changes | Owned by |
|---|---|---|---|
| **Slot** | `safety-3`, its worktree paths, its ports | never | Sprig |
| **Purpose** | label "Risk matrix overrides", a status note, tags | whenever the holder likes | the claimer |
| **Git state** | the branch (or detached ref) in each repo | whenever git or `ws switch` does | git, read live |

### Changes

- **The branch is observed, not stored.** `ls`, `info`, `here` and the app read each worktree's current branch from git. Reading the worktree's `.git` file and then its `HEAD` is a couple of small file reads, so listing many workspaces spawns no `git` processes. The record keeps only the **claim branch** (the branch Sprig last cut or checked out), which release uses to report unpushed commits.
- **A claim no longer requires a branch.** `pool checkout --detached` claims a workspace parked at a ref, for reviewing or reproducing. A workspace may also hold different branches per repo; its identity is unaffected.
- **Purpose is the headline and can be edited.** `sprig ws label <ws> "<text>"` and `sprig ws note <ws> "<status>"` can run at any time. An unlabelled workspace shows its live branch in place of a label, so it still reads sensibly.
- **`ls` leads with purpose:**

  ```
  safety-3  ● Risk matrix overrides   SFTY-2231-risk-overrides   client :8103   claimed 2h
  safety-1  ○ (free)                  detached @ origin/main                    released 1d
  ```

### Seeing it outside Sprig

- `sprig cd` sets the new terminal's title to the label.
- `sprig code <ws>` (new) writes a `.code-workspace` file into the central store (never the repo). It sets the window title to the label and tints the title bar with the slot's colour, so open VS Code windows are distinguishable. The colour belongs to the slot, so it stays the same across claims, and can be changed with `sprig ws colour`.
- The app shows the label as the card title with branch and note beneath.

## 2. Tags

Namespaced key/value pairs on a claim, so a caller can mark the workspaces it owns and find them again:

```sh
sprig pool checkout safety --branch SFTY-2231-risk-overrides --tag agent.session=7f3a --tag ci.run=4812
sprig ws tag safety-3 review.pr=41
sprig ws untag safety-3 review.pr
sprig ws ls --tag agent.session=7f3a --json
```

- Keys are `<namespace>.<name>`, lower case, with letters, digits and `-`. Values are strings.
- Sprig stores them on the claim, shows them in `info` and `here`, filters on them in `ls` (repeat `--tag` to require every one), and returns them in `--json`.
- Release clears them from the claim but keeps them in the recorded release report, so a caller can still see what it released.
- Sprig never acts on a tag. One that needs behaviour becomes a first-class field instead (the label and note are examples).

### Cleanup belongs to the caller

Sprig does not decide whether a claim is abandoned. A caller that knows its own liveness (a session ID, a CI run, a PID) tags its claims with it, lists them by tag, and releases the ones it knows are dead. This is safe to script because release already stops containers rather than removing them, touches no git, and reports uncommitted and unpushed work.

## 3. Switching a workspace's git state in place

Sprig cannot know whether a PR has merged or is only queued, and should not try. So moving a worktree on and tidying up the old branch are separate jobs: the workspace moves on straight away, and the old branch stays in git for whoever cleans it up later.

```sh
sprig ws switch safety-3 --new SFTY-2240-audit-log --from origin/main   # cut a new branch from a ref
sprig ws switch safety-3 --branch SFTY-2199-hotfix                      # check out an existing branch
sprig ws switch safety-3 --detach origin/main                           # park at a ref, no branch
```

- Applies to every repo in the workspace, or to `--only <repos>`. `--from` defaults to each repo's base branch.
- `--branch` accepts any branch git knows about, local or remote-tracking (fetched first). At a terminal with no branch given it offers a picker of known branches, filtered as you type.
- The previous branch is left exactly where it is: not deleted, reset or pushed.
- It refuses when a worktree has uncommitted changes, naming the files, as git would. Unpushed commits on the previous branch are reported but do not block, because the branch keeps them.
- Git refuses to check out a branch that another worktree already has. Sprig reports which workspace holds it instead of git's raw error.
- The claim branch becomes the new branch. The note, tags, slot and ports are unchanged, and so is an explicitly set label. A label is never filled in implicitly, so an unlabelled workspace shows the new branch straight away.
- `.env` is regenerated and `setup` reruns, with the same soft-failure rules as checkout. Infra is left running.
- `--fresh` also resets the workspace's data volumes, for a change that needs a clean database. Infra is recreated rather than left running.

This is the in-place sibling of `ws refresh`, which resets to the base branch and discards local commits with `--force`. `switch` never discards commits.

## 4. Discovery: where am I, where do I go

### `sprig here`

`sprig here [--json]` resolves the current directory (or `--path <dir>`) to its workspace, repo and module by matching it against the records. It writes no marker file into the worktree. Outside a workspace it exits non-zero with a short message, so callers can stay quiet.

Statuslines, shell prompts, hooks, VS Code tasks and session monitors such as perch can all call it. Because those callers parse it, the JSON is a contract: it carries a `schema` version and only gains fields.

```jsonc
{ "schema": 1,
  "workspace": "safety-3", "map": "safety", "repo": "client", "module": "spa",
  "label": "Risk matrix overrides", "note": "waiting on review", "colour": "#3b82f6",
  "tags": { "agent.session": "7f3a" },
  "claimed": true, "claimedAt": "2026-10-28T09:12:00Z",
  "repos": [ { "name": "client", "branch": "SFTY-2231-risk-overrides", "path": "…" } ],
  "ports": { "client-spa.port": 8103 },
  "urls":  [ { "capability": "client-spa", "url": "http://localhost:8103" } ] }
```

### URLs as declared outputs

A provided capability already has shapes such as `url`. Two optional fields make them navigable:

```jsonc
{ "capability": "client-spa",
  "ports":  { "port": { "allowed": "8100-8104" } },
  "shapes": { "url": "http://localhost:${sprig.client-spa.port}" },
  "open":   "url",
  "probe":  "${sprig.client-spa.url}" }
```

- `open` names the shape that is a browsable entry point.
- `probe` is a URL Sprig may GET to report the service as up or down. It is checked only when asked (`--probe`), never polled.
- `sprig ws urls <ws>` lists every `open` URL, and `sprig ws browse <ws> [capability]` opens one in the browser. (`sprig open` already launches the desktop app.)
- `info`, `here` and the app show the same list.

## How a caller uses this

An illustration, not part of Sprig: how an agent's delivery loop might drive these primitives.

| Step | Sprig call |
|---|---|
| start work | `sprig pool checkout <map> --branch <branch> --label "<title>" --tag agent.session=<id> --json --ni` |
| move the agent in | the harness's own "enter worktree" step, given the path from `sprig path` |
| statusline / banner | `sprig here --json`, to show the label and the entry URL |
| find its workspaces | `sprig ws ls --tag agent.session=<id> --json` |
| next piece of work | `sprig ws switch <ws> --new <branch> --from origin/main --json --ni` |
| close out | `sprig pool release`, whose pending-work report doubles as a last check |
| housekeeping | list by tag, release those whose session the caller knows has ended |

Nothing in the table requires Sprig to know what a session, ticket or PR is.

## Order of work

1. **Identity layers (1) and `sprig here` (4, first half).** Together they answer "what is each workspace doing" and "where am I". This is the change most visible to a person, and the one perch would use first.
2. **Tags (2).** Small, and everything a caller needs to own and clean up its workspaces.
3. **In-place switching (3).** Gets a workspace back to work after a merge, and adds checking out an existing branch.
4. **URLs and probes (4, second half).** Small, and answers "which URL do I go to".
5. **VS Code window title and colour (1).** Polish once the label is reliable.

## Deferred

- **Shared providers with per-workspace namespaces.** Deriving a vhost or database name per workspace on a broker or server every workspace shares, with `provision`/`deprovision` commands. Needs more refinement, including where provisioning credentials come from and whether template expansion in commands is acceptable when `setup` deliberately has none.
- **Worktree location as a template.** A setting for where worktrees live, including Claude Code's `{repo}/.claude/worktrees/{ws}` layout, optionally with an NTFS junction at today's sibling path for navigation. Needs care: the real path must stay canonical, `ws rm` must remove a junction without following it, and the main checkout's ignores, watchers and Docker build contexts must exclude the nested folder.
- **A machine-wide running budget and reclaiming idle infra.** Capping how many workspaces have infra running and stopping idle ones. Depends on settling whether Sprig has any say in how things are run, which it ideally does not.

## Explicitly out of scope

- **Deciding a claim is abandoned.** No holders, leases or reaping. Callers tag their claims and release what they know is dead.
- **Knowing about PRs or merges.** `switch` moves a workspace on; deleting merged branches is for git, the forge or the caller.
- **Running the apps.** Sprig manages infra and configuration; developers and callers run the apps, so a plain `dotnet run` stays the way to start one.
- **Interpreting process concepts.** No tickets, slices, PRs or sessions in Sprig's model. They exist only as tag values Sprig does not read.
- **Calling out.** Callers call Sprig; Sprig does not call them. An event stream (`sprig ws watch --json`) can be added later if polling proves too slow.
