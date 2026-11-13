# Add a per-repo `setup` step to `.sprig.json`

## Context

Today a sprig workspace is materialised as: git worktree → clobbered `.env.*` →
generated compose → recorded instance. What's missing is the **"now install the
project's dependencies"** step. When you spin up an isolated worktree, `node_modules`,
NuGet packages, Python venvs, etc. don't exist yet, so the worktree isn't actually
runnable until you `cd` in and run the install by hand.

This change lets a repo declare that setup **declaratively**, so it runs automatically
right after the worktree is created. It's the repo's own concern (like `env`/`compose`),
so it belongs in `.sprig.json`.

**Decisions confirmed with the user:**
- **Shape:** an ordered **list** of free-form commands (`"setup": ["npm ci", "dotnet restore"]`).
  Each entry runs in order, at the **worktree root**, via the platform shell.
- **On failure:** **warn & keep going** — a non-zero exit does *not* roll back the
  workspace. The workspace stays created; the failure is surfaced as a warning and
  recorded on the instance. (This is deliberately unlike the hard-fail+rollback that a
  bad worktree/env/compose triggers.)
- **Scope for v1:** commands are literal — **no `${sprig.*}` substitution** inside setup
  commands (can be added later if wanted).

## Model & data-flow changes (Sprig.Core)

### 1. `SprigRepoConfig` — new optional field
`src/Sprig.Core/Config/SprigRepoConfig.cs`

Add to the record:
```csharp
/// <summary>Free-form commands run in order at the worktree root after it's created
/// (e.g. "npm ci", "dotnet restore"). Each runs via the platform shell. A failing
/// command warns but does not roll back the workspace.</summary>
public IReadOnlyList<string> Setup { get; init; } = [];
```
No schema bump. `SupportedSchema` stays `2` — the validator does an **exact** schema
match (`config.Schema != SupportedSchema`), so bumping to 3 would reject every existing
`.sprig.json` on disk. The field is purely additive: absent → empty list → no setup.

### 2. Validation
`src/Sprig.Core/Config/SprigConfigValidator.cs`

Add `ValidateSetup`: each `setup[i]` must be non-empty/non-whitespace
(`setup[i]: "must be a non-empty command"`). No identifier rules — it's free-form. Wire
it into `Validate` alongside the existing `ValidateInputs/Env/Compose` calls.

### 3. New `SetupRunner`
New file `src/Sprig.Core/Setup/SetupRunner.cs`

Reuses the existing `IProcessRunner` seam (same one `GitService`/`DockerService` use).
```csharp
public sealed record SetupOutcome(string Command, int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

public sealed class SetupRunner(IProcessRunner runner)
{
    // Runs each command in order at workingDirectory via the platform shell.
    // NEVER throws on a non-zero exit — returns an outcome per command so the caller
    // decides what to do (we warn, not roll back). A shell that can't start is captured
    // as a failed outcome (exit -1) rather than thrown.
    public IReadOnlyList<SetupOutcome> Run(
        IReadOnlyList<string> commands, string workingDirectory, CancellationToken ct = default);
}
```
- Shell selection: Windows → `runner.Run("cmd.exe", ["/c", command], wd, ct)`;
  otherwise → `runner.Run("/bin/sh", ["-c", command], wd, ct)` (`OperatingSystem.IsWindows()`).
- Output stored = combined stdout+stderr, capped (~4000 chars, keep the tail) so instance
  records stay lean. On success we can store an empty `Output`; on failure keep the tail.
- Known limitation (note in code): `cmd.exe /c` with complex quoting/`&&` in a *single*
  entry can be finicky — simple per-command entries are the happy path.

### 4. Persist the outcome on the instance
`src/Sprig.Core/Store/InstanceRecord.cs`

Add to `InstanceRepo` (default empty → backward compatible with old records):
```csharp
/// <summary>Outcome of this repo's setup commands, in order (empty if none declared).</summary>
public IReadOnlyList<SetupOutcome> Setup { get; init; } = [];
```
This makes the warning available both to the immediate post-create call site (off the
returned record) and to the Workspaces view later.

### 5. Run setup during create
`src/Sprig.Core/Workspaces/WorkspaceService.cs`

- Add a trailing **optional** ctor param `SetupRunner? setup = null` to the primary
  constructor. Optional keeps existing test constructors compiling; when null, setup is
  skipped entirely.
- In `Create(...)`, inside the per-repo loop, after `env.Apply(...)` and compose
  generation, run that repo's setup **defensively** so it can never trip the rollback
  `catch`:
  ```csharp
  var setupOutcomes = (setup is not null && repo.Config.Setup.Count > 0)
      ? setup.Run(repo.Config.Setup, plan.Worktree)   // never throws
      : [];
  ```
  Store `Setup = setupOutcomes` on the `InstanceRepo`. A failing command therefore leaves
  the worktree/env/compose intact and the record saved — exactly "warn & keep going".
- Do **not** add setup work into the rollback block; a genuine worktree/env/compose
  failure still rolls back as before, before setup ever runs.

### 6. Wire the dependency
- `src/Sprig.App/AppServices.cs` — construct `new SetupRunner(runner)` and pass it into
  `new WorkspaceService(...)`.
- `src/Sprig.Cli/CliApp.cs` — same in the CLI composition root.

## CLI surfacing
`src/Sprig.Cli/CliApp.cs` → `Create(...)`

After printing each repo, print its setup outcomes: `✓`/`✗` per command, and the captured
output tail on failure. Include setup failures in the `--json` record automatically (it's
on the record now).

## App UI (Avalonia)

### 7. Editable setup section
`src/Sprig.App/ViewModels/RepoEditViewModel.cs`
- New `SetupCommandRow : ObservableObject` with `[ObservableProperty] string Command` and
  a `Remove` `[RelayCommand]` (mirrors `InputEditRow`).
- `ObservableCollection<SetupCommandRow> Setup { get; }` + `AddSetupCommand` relay command
  + `HasSetup`.
- `Load(...)`: populate from `c.Setup`.
- `Build()`: `Setup = Setup.Select(s => s.Command.Trim()).Where(c => c.Length > 0).ToList()`.

`src/Sprig.App/Views/ReposView.axaml` (edit modal, after the compose section)
- A `SETUP COMMANDS` section: header + `+ Add command`, an `ItemsControl` over `Setup`
  with a monospace `TextBox` bound `Command` two-way and a `✕` remove button (same row idiom
  as inputs/env).
- One-line helper subtext: *"Run in order at the worktree root after it's created (e.g.
  `npm ci`). Each runs via your shell; a failure warns but won't undo the workspace."*

### 8. Read-only setup display
- `src/Sprig.App/ViewModels/RepoConfigViewModel.cs` — add `IReadOnlyList<string> Setup` +
  `HasSetup`, loaded from `c.Setup`.
- `src/Sprig.App/Views/ReposView.axaml` (read-only panel) — a `SETUP` section listing the
  commands, gated on `SelectedConfig.HasSetup`, styled like the ENV/COMPOSE lists.

### 9. Surface the warning after create
Mirror the existing soft-warning pattern (the `infraWarning` handling in
`WorkspacesViewModel.Create`, lines ~159-176).
- `src/Sprig.App/ViewModels/WorkspacesViewModel.cs` (stack create) and
  `src/Sprig.App/ViewModels/ReposViewModel.cs` (`ConfirmIsolate`, single-repo path):
  after create, inspect `record.Repos.SelectMany(r => r.Setup).Where(o => !o.Success)`.
  If any failed, set the warning line (e.g. *"created 'X', but setup failed: `npm ci`
  (exit 1)"*) alongside the normal created-status message.

## Docs
`docs/config-reference.md`
- Add `setup` to the repo-config field table and a short `### setup[] — post-create
  commands` subsection (semantics, worktree-root, warn-not-fail, no substitution).
- Add step 4 to "Resolution at create time" (run setup; note it's a soft warning, no
  rollback).
- Also drop a copy of this feature note under `docs/` per the working-dir artefact rule.

## Tests (tests/Sprig.Tests)
- **SetupRunner** (new): picks `cmd.exe`/`/bin/sh` correctly, runs in the given working
  dir, returns one outcome per command in order, and does **not** throw on a non-zero exit
  (drive it with a fake/`RecordingProcessRunner`).
- **SprigConfigValidator**: blank `setup` entry rejected; valid list passes. Loader
  round-trips `setup`.
- **WorkspaceService** (extend `WorkspaceServiceTests`): a repo with `setup` records
  outcomes on the instance; a **failing** setup command still creates the workspace
  (no rollback) and records the failure. Pass a fake `SetupRunner`.
- **RepoEditViewModel**: `Load` → `Build` round-trips setup commands (blank rows dropped).

## Verification
1. `dotnet build` then `dotnet test` (all green, new tests included).
2. CLI smoke: add `"setup": ["node -v"]` to a test repo's `.sprig.json`, then
   `sprig create --repo <path> demo` → output shows the `✓ node -v` line; `sprig info demo`
   / the record JSON shows the recorded outcome. Try a failing command (e.g. `"exit 1"` /
   `"false"`) → workspace is still created, warning shown, worktree intact.
3. App smoke (`/run`): open the repo editor, add a couple of setup commands, save, confirm
   they persist in `.sprig.json` and render in the read-only view; Isolate the repo and
   confirm the setup runs and any failure surfaces as a soft warning (workspace still created).
```
```

## Out of scope (v1)
- `${sprig.*}` substitution inside setup commands.
- Live/streamed command output in the UI (output is captured and shown on failure).
- Re-running setup on an existing workspace (a `sprig setup <workspace>` command) — easy
  follow-up since `SetupRunner` + the recorded outcomes already exist.
