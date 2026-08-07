# CLI structure review

A review pass over the `sprig` CLI (`src/Sprig.Cli`) now that the feature set is
stable, plus the plan for tightening it. The CLI is a single dispatcher —
`CliApp.Run` — over `Sprig.Core`, running in parallel to the desktop app as a
second front-end onto the same engine.

## Verdict

The bones are right: commands map cleanly onto the core model
(repo → stack → workspace → infra), error handling is centralised in one
`try/catch`, and `--json` gives it a scripting story. The issues are **identity,
consistency, and a few gaps** rather than anything structurally broken.

## Findings

### 1. Identity mismatch (highest value, lowest effort)
`Program.cs` and `CliApp.cs` describe the CLI as *"an internal harness… Not the
shipped product."* That's stale. It ships on `PATH`, self-updates
(`sprig update`), launches the app (`sprig open`), and the README claims *"the
terminal covers everything the app does."* It **is** a supported surface — the
comments should say so, and the `--json` output shape should be treated as a
contract.

### 2. Overcomplicated / redundant
- **`templates` duplicates `stack ls`** — same stacks, different column
  formatting. Two commands for one concept.
- **Three overlapping inspection commands** — `info`, `status`, `reconcile`.
  The drift overlap between `info` and `reconcile` is the fuzzy one; defensible
  but worth being deliberate about.
- **Two dispatch styles** — flat verbs for workspaces (`create`, `up`, `down`),
  noun-grouped subcommands for `repo`/`stack`. Fine if intentional (workspaces
  are the primary object); the rule should just be named so new commands land
  consistently.

### 3. Inconsistencies
- **`--json` is global but ~half the handlers ignore it** (`up`, `down`,
  `reset`, `rm`, `reconcile --repair`, `repo add`, `stack create/import/export`,
  `init` write path). A script can't rely on it.
- **`stack show` always prints JSON**, even without `--json` — inconsistent with
  `stack ls`.
- **Destructive-op confirmation is uneven** — `rm` demands `--yes`, but
  `down --volumes` wipes data with no guard.
- **`stack create` is secretly an upsert** (calls `Save`, which overwrites) but
  there is no `stack edit`, and `Save` refuses once a workspace depends on the
  stack. The "create makes new / edit changes existing" model doesn't hold.

### 4. Gaps vs. the app
- **Settings / port range** (`SettingsViewModel`) — no CLI command.
- **Stack editing** — create/rm only; no incremental edit.
- (Env inspection/editing exists in the app; lower priority for the CLI.)

### 5. Robustness
- **Hand-rolled `Args`** — no unknown-flag detection (typos silently ignored),
  no `--flag=value` form, no `--` terminator.
- **Latent sharp edge:** `repo`/`stack` strip the subcommand with
  `args.Where(a => a != sub)`, which removes *every* occurrence of that string —
  a value equal to the subcommand name (or `--name ls`) gets silently dropped.
- **No CLI-level tests** — nothing exercises `CliApp` dispatch/parsing/exit
  codes.

## Plan

Ordered by value-for-effort. Checked off as landed on the `cli` branch.

- [x] **1. Identity story** — update the stale "internal harness" comments in
  `Program.cs`/`CliApp.cs`; state the CLI is a supported surface and that
  `--json` is a stability contract.
- [x] **2. `--json` consistency** — honour `--json` across every command
  (mutating ones emit a small `{ ok: true, … }` payload); fix `stack show`
  emitting JSON regardless of the flag.
- [ ] **3. Parity commands** — add `sprig settings` (view/set port range +
  restricted ports) and `stack edit` (re-save an existing stack, subject to the
  same dependent-workspace guard).
- [ ] **4. Retire `templates`** — drop the redundant command (fold into
  `stack ls`).
- [ ] **5. Harden arg parsing + tests** — support `--flag=value` and `--`,
  detect unknown flags, fix the subcommand-filtering edge; add CLI
  dispatch/exit-code tests.
