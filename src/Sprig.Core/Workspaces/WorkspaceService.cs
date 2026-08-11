using System.Text.RegularExpressions;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Docker;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Substitution;

namespace Sprig.Core.Workspaces;

/// <summary>Thrown when a workspace operation cannot proceed (bad input, conflict, invalid config).</summary>
public sealed class WorkspaceException(string message) : Exception(message);

/// <summary>
/// Orchestrates the single-repo workspace lifecycle: create → worktree + branch + clobbered
/// .env + generated compose + record; infra up/down/reset; teardown → layered/idempotent per
/// the S3 matrix (infra torn down first). Multi-repo stacks are added in M4.
/// </summary>
public sealed partial class WorkspaceService(
    IGitService git,
    IPortStore ports,
    InstanceStore instances,
    EnvClobberService env,
    ComposeGenerator compose,
    IDockerService docker,
    ISprigPaths paths,
    Setup.SetupRunner? setup = null)
{
    public const string ConfigFileName = ".sprig.json";
    public const string GeneratedComposeName = "docker-compose.sprig.yml";

    static string ProjectName(string workspace) => $"sprig-{workspace}";

    /// <summary>A filename-safe slug of a repo-relative compose path, so two compose files from
    /// different directories generate distinct names in the instance dir (e.g.
    /// <c>apps/web/docker-compose.yml</c> → <c>apps-web-docker-compose-yml</c>).</summary>
    static string ComposeSlug(string relFile)
    {
        var chars = relFile.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? slug : "compose";
    }

    public IReadOnlyList<InstanceRecord> List() => instances.LoadAll();
    public InstanceRecord? Get(string workspace) => instances.TryLoad(workspace);

    /// <summary>Generate one isolated compose copy per overridden compose file across the repo's modules,
    /// into the workspace's instance dir. The dest name folds in the repo, module and a slug of the source
    /// path so two files never collide. Shared by create and refresh so the naming stays identical.</summary>
    IReadOnlyList<string> GenerateComposeFiles(ResolvedRepo repo, string workspace, IVariableSource scope)
    {
        var composePaths = new List<string>();
        foreach (var module in repo.Config.EffectiveModules)
            foreach (var composeCfg in module.Compose)
            {
                var dest = Path.Combine(paths.InstanceDir(workspace),
                    $"docker-compose.{repo.Name}.{module.Name}.{ComposeSlug(composeCfg.File)}.sprig.yml");
                compose.GenerateToFile(
                    Path.Combine(repo.Root, module.Path, composeCfg.File), composeCfg, scope, dest);
                composePaths.Add(dest);
            }
        return composePaths;
    }

    /// <summary>Create an isolated workspace from a single ad-hoc repo. Rolls back on failure.</summary>
    public InstanceRecord Create(string repoPath, string workspace,
        IProgress<WorkspaceStepProgress>? progress = null)
        => Create(ResolveSingleRepo(repoPath), workspace, progress);

    /// <summary>The ordered checklist <see cref="Create(ResolvedStack, string, IProgress{WorkspaceStepProgress})"/>
    /// will work through, computed up front so a UI can show every row before execution starts. Runs the
    /// same cheap pre-flight validation as create, so a bad name / duplicate workspace fails here rather
    /// than mid-checklist.</summary>
    public IReadOnlyList<WorkspaceStep> PlanCreate(ResolvedStack stack, string workspace)
    {
        ValidateCreate(stack, workspace);
        var steps = new List<WorkspaceStep> { new(CreateStepIds.Ports, "Allocate ports") };
        foreach (var repo in stack.Repos)
        {
            steps.Add(new(CreateStepIds.Worktree(repo.Name), $"Create worktree — {repo.Name}"));
            steps.Add(new(CreateStepIds.Env(repo.Name), $"Apply environment — {repo.Name}"));
            if (repo.Config.EffectiveModules.Any(m => m.Compose.Count > 0))
                steps.Add(new(CreateStepIds.Compose(repo.Name), $"Generate compose — {repo.Name}"));
            if (HasSetup(repo))
            {
                // A parent "Install dependencies" row with one indented sub-row per command (across all
                // the repo's modules, in order), so each command's progress + live output is on its own line.
                steps.Add(new(CreateStepIds.Setup(repo.Name), $"Install dependencies — {repo.Name}"));
                foreach (var cmd in SetupCommands(repo))
                    steps.Add(new(CreateStepIds.SetupCommand(repo.Name, cmd.Index), cmd.Command) { SubStep = true });
            }
        }
        steps.Add(new(CreateStepIds.Record, "Save workspace record"));
        return steps;
    }

    /// <summary>Whether this repo has setup commands to run (and a runner to run them).</summary>
    bool HasSetup(ResolvedRepo repo) =>
        setup is not null && repo.Config.EffectiveModules.Any(m => m.Setup.Count > 0);

    /// <summary>The repo's setup commands flattened across its modules, in order, with a global index
    /// (so step ids line up between <see cref="PlanCreate"/> and <see cref="RunSetup"/>) and the module's
    /// path/name for the working directory and grouping. Blank commands are skipped but still consume an
    /// index, so the ids are stable regardless of blanks.</summary>
    static IEnumerable<(int Index, string Command, string ModulePath, string ModuleName)> SetupCommands(ResolvedRepo repo)
    {
        var i = -1;
        foreach (var module in repo.Config.EffectiveModules)
            foreach (var command in module.Setup)
            {
                i++;
                if (string.IsNullOrWhiteSpace(command)) continue;
                yield return (i, command, module.Path, module.Name);
            }
    }

    /// <summary>Run the repo's setup commands one at a time (each in its module's directory within the
    /// worktree), reporting each as its own sub-step and streaming its live output to the progress sink.
    /// Stops at the first failure (a later command usually depends on an earlier one); the failed
    /// command's row goes Warning — setup never rolls the workspace back — and any commands after it stay
    /// Pending (unreached).</summary>
    IReadOnlyList<Setup.SetupOutcome> RunSetup(ResolvedRepo repo, string worktree,
        IProgress<WorkspaceStepProgress>? progress)
    {
        var outcomes = new List<Setup.SetupOutcome>();
        foreach (var (index, command, modulePath, moduleName) in SetupCommands(repo))
        {
            var cwd = string.IsNullOrEmpty(modulePath)
                ? worktree
                : Path.Combine(worktree, modulePath.Replace('/', Path.DirectorySeparatorChar));
            var id = CreateStepIds.SetupCommand(repo.Name, index);
            progress?.Report(new(id, WorkspaceStepState.Running));
            var outcome = setup!.RunCommand(command, cwd,
                onOutput: line => progress?.Report(new(id, WorkspaceStepState.Running) { Output = line }))
                with { Module = moduleName };
            outcomes.Add(outcome);
            progress?.Report(outcome.Success
                ? new(id, WorkspaceStepState.Done)
                : new(id, WorkspaceStepState.Warning, $"exited {outcome.ExitCode}"));
            if (!outcome.Success) break;
        }
        return outcomes;
    }

    /// <summary>Create an isolated workspace from a resolved stack (1+ repos). Rolls back on failure.
    /// Reports checklist progress to <paramref name="progress"/> if supplied (steps match
    /// <see cref="PlanCreate"/>). A partial stack (see <see cref="ResolvedStack.ExcludedRepos"/>)
    /// needs no special handling here: it arrives already narrowed, so only its repos are
    /// materialised and only its ports are allocated.</summary>
    public InstanceRecord Create(ResolvedStack stack, string workspace,
        IProgress<WorkspaceStepProgress>? progress = null)
    {
        ValidateCreate(stack, workspace);

        var branch = BranchFor(workspace);

        // Pre-compute each repo's sibling worktree path and guard against collisions.
        var plans = new List<RepoPlan>();
        foreach (var repo in stack.Repos)
        {
            var parent = Directory.GetParent(repo.Root)?.FullName
                ?? throw new WorkspaceException($"repo '{repo.Root}' has no parent directory for a sibling worktree");
            var dirName = Path.GetFileName(repo.Root.TrimEnd('\\', '/'));
            var worktree = Path.Combine(parent, $"{dirName}--{workspace}");
            if (Directory.Exists(worktree))
                throw new WorkspaceException($"worktree path already exists: {worktree}");
            plans.Add(new RepoPlan(repo, worktree));
        }

        var portsAcquired = false;
        var addedWorktrees = new List<(string root, string worktree)>();
        // The step whose real work is currently in flight, so the catch can paint the right row red.
        var current = CreateStepIds.Ports;
        try
        {
            // The stack owns the ports; allocate one real non-colliding number per named port.
            // A repo input may pin its port to a fixed set (e.g. pre-registered Auth0 callbacks);
            // resolve those onto the stack ports so allocation only draws from the allowed set.
            progress?.Report(new(CreateStepIds.Ports, WorkspaceStepState.Running));
            var constraints = PortConstraintResolver.Resolve(stack.Repos, stack.Bindings, stack.Ports);
            var requests = stack.Ports
                .Select(p => new PortRequest(p, constraints.GetValueOrDefault(p)))
                .ToList();
            var allPorts = ports.Acquire(workspace, requests);
            portsAcquired = true;

            // Resolve per-repo input scopes from the stack's bindings (hard-fails on an unbound input).
            var wired = StackWiring.Resolve(workspace, allPorts, stack.Repos, stack.Bindings);
            progress?.Report(new(CreateStepIds.Ports, WorkspaceStepState.Done));

            var repoRecords = new List<InstanceRepo>();
            foreach (var plan in plans)
            {
                var repo = plan.Repo;
                var repoScope = wired.ScopeFor(repo.Name);

                current = CreateStepIds.Worktree(repo.Name);
                progress?.Report(new(current, WorkspaceStepState.Running));
                git.AddWorktree(repo.Root, plan.Worktree, branch);
                addedWorktrees.Add((repo.Root, plan.Worktree));
                progress?.Report(new(current, WorkspaceStepState.Done));

                current = CreateStepIds.Env(repo.Name);
                progress?.Report(new(current, WorkspaceStepState.Running));
                env.Apply(repo.Config, repo.Root, plan.Worktree, repoScope);
                progress?.Report(new(current, WorkspaceStepState.Done));

                // A repo may override several compose files across its modules; generate one isolated copy
                // per file, named with the module + a slug of the source path so two files never collide in
                // the instance dir (the same filename in two modules stays distinct). Each source path is
                // resolved under its module's directory.
                IReadOnlyList<string> composePaths = [];
                if (repo.Config.EffectiveModules.Any(m => m.Compose.Count > 0))
                {
                    current = CreateStepIds.Compose(repo.Name);
                    progress?.Report(new(current, WorkspaceStepState.Running));
                    composePaths = GenerateComposeFiles(repo, workspace, repoScope);
                    progress?.Report(new(current, WorkspaceStepState.Done));
                }

                // Install the repo's dependencies in the fresh worktree. This is the last step and
                // deliberately soft: SetupRunner never throws on a non-zero exit, so a failed install
                // is recorded (and surfaced here as a Warning) but does NOT trip the rollback below —
                // the worktree/env/compose are already good and worth keeping.
                IReadOnlyList<Setup.SetupOutcome> setupOutcomes = [];
                if (HasSetup(repo))
                {
                    var setupStep = CreateStepIds.Setup(repo.Name);
                    progress?.Report(new(setupStep, WorkspaceStepState.Running));
                    setupOutcomes = RunSetup(repo, plan.Worktree, progress);
                    var failed = setupOutcomes.FirstOrDefault(o => !o.Success);
                    progress?.Report(failed is null
                        ? new(setupStep, WorkspaceStepState.Done)
                        : new(setupStep, WorkspaceStepState.Warning, $"'{failed.Command}' exited {failed.ExitCode} — worktree kept"));
                }

                repoRecords.Add(new InstanceRepo
                {
                    Name = repo.Name,
                    SourcePath = repo.Root,
                    WorktreePath = plan.Worktree,
                    Branch = branch,
                    GeneratedComposePaths = composePaths,
                    Inputs = wired.Inputs[repo.Name],
                    Setup = setupOutcomes,
                });
            }

            current = CreateStepIds.Record;
            progress?.Report(new(current, WorkspaceStepState.Running));
            var record = new InstanceRecord
            {
                Workspace = workspace,
                Stack = stack.StackName,
                Repos = repoRecords,
                Ports = new Dictionary<string, int>(allPorts),
                ExcludedRepos = stack.ExcludedRepos,
                SkippedPorts = stack.SkippedPorts,
                LastStatus = "created",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            instances.Save(record);
            progress?.Report(new(current, WorkspaceStepState.Done));
            return record;
        }
        catch (Exception ex)
        {
            progress?.Report(new(current, WorkspaceStepState.Error, ex.Message));
            // Best-effort rollback across every repo materialised so far.
            foreach (var (root, worktree) in addedWorktrees)
            {
                TryQuiet(() => git.RemoveWorktree(root, worktree));
                TryQuiet(() => git.DeleteBranch(root, branch));
                WorktreeInspector.TryDeleteDirectory(worktree);
            }
            if (portsAcquired) TryQuiet(() => ports.Release(workspace));
            TryQuiet(() => instances.Delete(workspace));
            throw;
        }
    }

    /// <summary>Shared cheap pre-flight validation for create (used by both create and its planner).</summary>
    void ValidateCreate(ResolvedStack stack, string workspace)
    {
        ValidateName(workspace);
        if (stack.Repos.Count == 0)
            throw new WorkspaceException("nothing to create: the stack has no repos");
        if (instances.TryLoad(workspace) is not null)
            throw new WorkspaceException($"workspace '{workspace}' already exists");

        // The branch sprig will cut for each repo. A flat 'sprig--<ws>' name (no '/') can't hit git's
        // directory/file ref conflict, so the only way create fails on it is if that exact branch already
        // exists — catch it here with a clear message rather than letting `git worktree add` throw a raw fatal.
        var branch = BranchFor(workspace);
        foreach (var repo in stack.Repos)
            if (git.BranchExists(repo.Root, branch))
                throw new WorkspaceException(
                    $"branch '{branch}' already exists in repo '{repo.Name}' — delete or rename it, " +
                    "or choose a different workspace name");
    }

    /// <summary>The branch sprig cuts for a workspace. Flat <c>sprig--&lt;ws&gt;</c> (no <c>/</c>) so it can
    /// never hit git's directory/file ref conflict the way a <c>sprig/&lt;ws&gt;</c> name would against a
    /// plain <c>sprig</c> branch. Mirrors the sibling worktree folder convention (<c>&lt;dir&gt;--&lt;ws&gt;</c>).</summary>
    internal static string BranchFor(string workspace) => $"sprig--{workspace}";

    /// <summary>Resolve an ad-hoc single repo path into a one-repo stack.</summary>
    public ResolvedStack ResolveSingleRepo(string repoPath)
    {
        if (!git.IsGitRepo(repoPath))
            throw new WorkspaceException($"'{repoPath}' is not a git repository");
        var root = git.ResolveRepoRoot(repoPath);
        var config = LoadValidConfig(root);
        // Ad-hoc single repo: no stack, so only zero-input repos can stand up this way.
        return new ResolvedStack(null, [new ResolvedRepo(config.Name, root, config)],
            [], new Dictionary<string, IReadOnlyDictionary<string, string>>());
    }

    sealed record RepoPlan(ResolvedRepo Repo, string Worktree);

    /// <summary>The ordered checklist <see cref="Remove(string, bool, IProgress{WorkspaceStepProgress})"/>
    /// will work through for the given record, computed up front so a UI can show every row before
    /// teardown starts.</summary>
    public IReadOnlyList<WorkspaceStep> PlanRemove(InstanceRecord record, bool force)
    {
        var steps = new List<WorkspaceStep>();
        foreach (var repo in record.Repos.Where(r => r.ComposePaths.Count > 0))
            steps.Add(new(RemoveStepIds.Infra(repo.Name), $"Stop containers — {repo.Name}"));
        foreach (var repo in record.Repos)
        {
            steps.Add(new(RemoveStepIds.Worktree(repo.Name), $"Remove worktree — {repo.Name}"));
            if (force && repo.Branch is not null)
                steps.Add(new(RemoveStepIds.Branch(repo.Name), $"Delete branch — {repo.Name}"));
        }
        steps.Add(new(RemoveStepIds.Ports, "Release ports"));
        steps.Add(new(RemoveStepIds.Record, "Delete workspace record"));
        return steps;
    }

    /// <summary>
    /// Tear down a workspace. Layered and idempotent: each step tolerates its target already
    /// being gone, so re-running a teardown is always safe. The branch is deleted only when
    /// <paramref name="force"/> is set. Reports checklist progress to <paramref name="progress"/>
    /// if supplied (steps match <see cref="PlanRemove"/>); because teardown is best-effort, a step
    /// whose action throws is reported as a Warning, not an Error — the sweep always runs to
    /// completion. The record is removed last, and <b>only when every step succeeded</b>: if any
    /// step warned, the record is kept and flagged <see cref="InstanceRecord.TeardownFailed"/> so
    /// the workspace stays visible and the teardown can be retried once the blocker is fixed.
    /// </summary>
    public void Remove(string workspace, bool force = false, IProgress<WorkspaceStepProgress>? progress = null)
    {
        var record = instances.TryLoad(workspace);
        if (record is null)
        {
            // No record — still release any stray port lease so nothing leaks.
            TryQuiet(() => ports.Release(workspace));
            return;
        }

        // What each best-effort step couldn't finish. Empty at the end means a clean sweep (delete
        // the record); anything here means we keep the record flagged for a later retry.
        var issues = new List<string>();

        // Step 1 of the S3 matrix: infra down (and wipe volumes) before touching worktrees.
        var dockerUp = docker.IsAvailable();
        foreach (var repo in record.Repos.Where(r => r.ComposePaths.Count > 0))
        {
            var id = RemoveStepIds.Infra(repo.Name);
            if (!dockerUp)
            {
                // Containers left running is a real reason not to finish teardown — flag it so the
                // record is kept and the user can retry once Docker is back.
                var note = $"stop containers ({repo.Name}): Docker unavailable — containers not stopped";
                progress?.Report(new(id, WorkspaceStepState.Warning, "Docker unavailable — containers not stopped"));
                issues.Add(note);
                continue;
            }
            // A prior partial sweep may already have removed the worktree, so the project directory
            // can be gone on retry. Containers are found by project name regardless, so fall back to
            // the (still-present) instance dir to give compose a real directory to run in.
            var projectDir = Directory.Exists(repo.WorktreePath) ? repo.WorktreePath : paths.InstanceDir(workspace);
            Step(progress, id, issues, $"stop containers ({repo.Name})", () =>
                docker.Down(repo.ComposePaths, projectDir, ProjectName(workspace), removeVolumes: true));
        }

        foreach (var repo in record.Repos)
        {
            var isRepo = git.IsGitRepo(repo.SourcePath);

            Step(progress, RemoveStepIds.Worktree(repo.Name), issues, $"remove worktree ({repo.Name})", () =>
            {
                var state = WorktreeInspector.Classify(git, repo.SourcePath, repo.WorktreePath);
                switch (state)
                {
                    case WorktreeState.Healthy when isRepo:
                        TryQuiet(() => git.RemoveWorktree(repo.SourcePath, repo.WorktreePath));
                        break;
                    case WorktreeState.MissingFolder when isRepo:
                        TryQuiet(() => git.Prune(repo.SourcePath));
                        break;
                    case WorktreeState.Orphaned:
                        WorktreeInspector.TryDeleteDirectory(repo.WorktreePath);
                        if (isRepo) TryQuiet(() => git.Prune(repo.SourcePath));
                        break;
                    case WorktreeState.Gone:
                        break;
                }

                // Guarantee the folder is gone even if the git remove left it (or wasn't a repo).
                WorktreeInspector.TryDeleteDirectory(repo.WorktreePath);

                // A folder that survives every attempt is a real problem worth a yellow row.
                if (Directory.Exists(repo.WorktreePath))
                    throw new WorkspaceException($"worktree folder could not be removed: {repo.WorktreePath}");
            });

            if (force && repo.Branch is not null)
                Step(progress, RemoveStepIds.Branch(repo.Name), issues, $"delete branch ({repo.Name})", () =>
                {
                    if (isRepo && git.BranchExists(repo.SourcePath, repo.Branch))
                        git.DeleteBranch(repo.SourcePath, repo.Branch);
                });
        }

        Step(progress, RemoveStepIds.Ports, issues, "release ports", () => ports.Release(workspace));

        // Last step: delete the record only if everything above succeeded. If any layer warned,
        // keep the record — flagged, with the reasons — so the workspace still shows in the list and
        // a later `rm` resumes the sweep. Saving the flag is itself best-effort.
        if (issues.Count == 0)
        {
            Step(progress, RemoveStepIds.Record, issues, "delete workspace record", () => instances.Delete(workspace));
        }
        else
        {
            progress?.Report(new(RemoveStepIds.Record, WorkspaceStepState.Warning,
                "kept — teardown incomplete; fix the above and run rm again"));
            TryQuiet(() => instances.Save(record with { TeardownFailed = true, TeardownIssues = issues }));
        }
    }

    /// <summary>Run one best-effort teardown step, reporting Running → Done, or Warning if it throws
    /// (teardown never aborts on a single failed layer — the exception is surfaced, not propagated).
    /// A throw also appends a note to <paramref name="issues"/> under the label <paramref name="what"/>,
    /// which is what decides whether the record is kept and flagged at the end.</summary>
    static void Step(IProgress<WorkspaceStepProgress>? progress, string id, List<string> issues, string what, Action action)
    {
        progress?.Report(new(id, WorkspaceStepState.Running));
        try
        {
            action();
            progress?.Report(new(id, WorkspaceStepState.Done));
        }
        catch (Exception ex)
        {
            progress?.Report(new(id, WorkspaceStepState.Warning, ex.Message));
            issues.Add($"{what}: {ex.Message}");
        }
    }

    /// <summary>Bring the workspace's infra up.</summary>
    public void Up(string workspace)
    {
        var record = RequireWithInfra(workspace, out var infraRepos);
        foreach (var repo in infraRepos)
            docker.Up(repo.ComposePaths, repo.WorktreePath, ProjectName(workspace));
        instances.Save(record with { LastStatus = "running" });
    }

    /// <summary>Stop the workspace's infra; <paramref name="removeVolumes"/> wipes data.</summary>
    public void Down(string workspace, bool removeVolumes = false)
    {
        var record = RequireWithInfra(workspace, out var infraRepos);
        foreach (var repo in infraRepos)
            docker.Down(repo.ComposePaths, repo.WorktreePath, ProjectName(workspace), removeVolumes);
        instances.Save(record with { LastStatus = "stopped" });
    }

    /// <summary>Restart the workspace's infra (down then up, keeping volumes).</summary>
    public void RestartInfra(string workspace)
    {
        Down(workspace);
        Up(workspace);
    }

    /// <summary>Bring infra up if there's any to run and Docker is reachable; otherwise a no-op.
    /// Unlike <see cref="Up"/>, this never throws on a repo-only workspace or a stopped Docker — the
    /// pool/refresh flows want "make it running if possible", not a hard requirement. Returns whether
    /// containers were actually started.</summary>
    public bool TryStartInfra(string workspace)
    {
        var record = instances.TryLoad(workspace) ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        var infraRepos = record.Repos.Where(r => r.ComposePaths.Count > 0).ToList();
        if (infraRepos.Count == 0 || !docker.IsAvailable()) return false;
        foreach (var repo in infraRepos)
            docker.Up(repo.ComposePaths, repo.WorktreePath, ProjectName(workspace));
        instances.Save(record with { LastStatus = "running" });
        return true;
    }

    /// <summary>Stop infra if there's any and Docker is reachable; otherwise a no-op. <paramref name="removeVolumes"/>
    /// wipes data. The tolerant counterpart to <see cref="Down"/>, for the pool/refresh flows.</summary>
    public bool TryStopInfra(string workspace, bool removeVolumes = false)
    {
        var record = instances.TryLoad(workspace) ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        var infraRepos = record.Repos.Where(r => r.ComposePaths.Count > 0).ToList();
        if (infraRepos.Count == 0 || !docker.IsAvailable()) return false;
        foreach (var repo in infraRepos)
            docker.Down(repo.ComposePaths, repo.WorktreePath, ProjectName(workspace), removeVolumes);
        instances.Save(record with { LastStatus = "stopped" });
        return true;
    }

    /// <summary>Deprecated alias for <see cref="RestartInfra"/> — the CLI verb was renamed
    /// <c>ws reset</c> → <c>ws restart</c> when <c>reset</c> came to mean the git resync. Kept so
    /// existing callers/scripts keep working for one release.</summary>
    public void Reset(string workspace) => RestartInfra(workspace);

    /// <summary>
    /// Resync a workspace's repos to their base branch and rebuild its sprig-managed state, without
    /// throwing away the expensive on-disk artifacts. Per repo: fetch, hard-reset the worktree branch
    /// to base (tracked files only — gitignored node_modules/build output/real .env values survive),
    /// re-clobber env, regenerate compose, and re-run setup; then infra is restarted (volumes kept).
    /// <para>
    /// Works purely from the stored <see cref="InstanceRecord"/> (like teardown does), so it needs no
    /// stack resolver. <paramref name="onlyRepos"/> narrows the refresh to a subset; the rest are left
    /// exactly as they are. A refresh discards commits the base doesn't contain, so it refuses (listing
    /// the offenders) unless <paramref name="force"/> is set — work is never lost silently.
    /// </para>
    /// </summary>
    public InstanceRecord RefreshToBase(string workspace, IReadOnlyList<string>? onlyRepos = null,
        bool force = false, bool removeVolumes = false, IProgress<WorkspaceStepProgress>? progress = null)
    {
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");

        var targets = SelectRefreshRepos(record, onlyRepos);
        var targetNames = new HashSet<string>(targets.Select(r => r.Name), StringComparer.Ordinal);

        // Pre-flight across every target: fetch, resolve its base, and collect any that carry commits the
        // base doesn't — a hard-reset would discard those. Fail once, before touching anything, so a
        // guarded refresh never leaves a half-reset workspace.
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        var unmerged = new List<string>();
        foreach (var repo in targets)
        {
            TryQuiet(() => git.Fetch(repo.WorktreePath));
            var baseRef = git.ResolveDefaultBase(repo.WorktreePath);
            bases[repo.Name] = baseRef;
            if (git.CountCommitsAhead(repo.WorktreePath, baseRef) > 0)
                unmerged.Add($"{repo.Name} (has commits not in {baseRef})");
        }
        if (unmerged.Count > 0 && !force)
            throw new WorkspaceException(
                "refusing to refresh — a refresh resets each repo to its base branch, which would " +
                "discard commits in:\n  " + string.Join("\n  ", unmerged) +
                "\nmerge or push them first, or pass --force to discard them.");

        var updatedRepos = new List<InstanceRepo>();
        foreach (var repo in record.Repos)
        {
            if (!targetNames.Contains(repo.Name)) { updatedRepos.Add(repo); continue; }

            // Reload the committed config from source (it may have moved on with the base), and rebuild
            // the input scope from the values the record already stores.
            var config = LoadValidConfig(repo.SourcePath);
            var resolved = new ResolvedRepo(repo.Name, repo.SourcePath, config);
            var scope = ScopeFromInputs(workspace, repo.Inputs);

            git.ResetHard(repo.WorktreePath, bases[repo.Name]);
            env.Apply(config, repo.SourcePath, repo.WorktreePath, scope);

            var composePaths = config.EffectiveModules.Any(m => m.Compose.Count > 0)
                ? GenerateComposeFiles(resolved, workspace, scope)
                : repo.ComposePaths;

            var setupOutcomes = HasSetup(resolved) ? RunSetup(resolved, repo.WorktreePath, progress) : [];

            updatedRepos.Add(repo with { GeneratedComposePaths = composePaths, Setup = setupOutcomes });
        }

        var refreshed = record with { Repos = updatedRepos, LastStatus = "refreshed" };
        instances.Save(refreshed);

        // Restart infra so the regenerated compose takes effect; a fresh checkout wipes volumes first
        // (clean runtime data), the others keep them. Both helpers are tolerant — a repo-only workspace
        // or a stopped Docker just skips, so the refresh itself still succeeds.
        TryStopInfra(workspace, removeVolumes);
        TryStartInfra(workspace);

        return instances.TryLoad(workspace) ?? refreshed;
    }

    /// <summary>The repos a refresh will touch: all of them, or the named subset. An unknown name is an
    /// error (a typo shouldn't silently refresh nothing).</summary>
    static IReadOnlyList<InstanceRepo> SelectRefreshRepos(InstanceRecord record, IReadOnlyList<string>? onlyRepos)
    {
        if (onlyRepos is null || onlyRepos.Count == 0) return record.Repos;
        var known = new HashSet<string>(record.Repos.Select(r => r.Name), StringComparer.Ordinal);
        var unknown = onlyRepos.Where(n => !known.Contains(n)).ToList();
        if (unknown.Count > 0)
            throw new WorkspaceException(
                $"workspace '{record.Workspace}' has no repo{(unknown.Count == 1 ? "" : "s")} " +
                string.Join(", ", unknown.Select(n => $"'{n}'")) +
                $" (it has: {string.Join(", ", record.Repos.Select(r => r.Name))})");
        var wanted = new HashSet<string>(onlyRepos, StringComparer.Ordinal);
        return record.Repos.Where(r => wanted.Contains(r.Name)).ToList();
    }

    /// <summary>Rebuild the env/compose substitution scope for a repo from the input values the record
    /// stores. Mirrors <see cref="StackWiring"/>'s per-repo scope: the declared inputs plus
    /// <c>workspace</c>, which templates reference as <c>${sprig.workspace}</c>.</summary>
    static IVariableSource ScopeFromInputs(string workspace, IReadOnlyDictionary<string, string> inputs)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["workspace"] = workspace };
        foreach (var (key, value) in inputs) values[key] = value;
        return new DictionaryVariableSource(values);
    }

    /// <summary>Live container status across the workspace's infra repos.</summary>
    public IReadOnlyList<ContainerStatus> Status(string workspace)
    {
        var record = RequireWithInfra(workspace, out var infraRepos);
        _ = record;
        return infraRepos
            .SelectMany(r => docker.Ps(r.ComposePaths, r.WorktreePath, ProjectName(workspace)))
            .ToList();
    }

    InstanceRecord RequireWithInfra(string workspace, out IReadOnlyList<InstanceRepo> infraRepos)
    {
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        infraRepos = record.Repos.Where(r => r.ComposePaths.Count > 0).ToList();
        if (infraRepos.Count == 0)
            throw new WorkspaceException($"workspace '{workspace}' has no docker infrastructure");
        if (!docker.IsAvailable())
            throw new WorkspaceException(
                "docker compose is not available — is Docker Desktop installed and running?");
        return record;
    }

    SprigRepoConfig LoadValidConfig(string repoRoot)
    {
        var config = SprigConfigLoader.LoadFromFile(Path.Combine(repoRoot, ConfigFileName));
        var validation = SprigConfigValidator.Validate(config);
        if (!validation.IsValid)
            throw new WorkspaceException(
                "invalid .sprig.json:\n  " + string.Join("\n  ", validation.Issues));
        return config;
    }

    static void ValidateName(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || workspace is "." or ".." || !NamePattern().IsMatch(workspace))
            throw new WorkspaceException(
                $"invalid workspace name '{workspace}' (use letters, digits, '.', '-', '_')");
    }

    static void TryQuiet(Action action)
    {
        try { action(); } catch { /* teardown/rollback is best-effort per layer */ }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex NamePattern();
}
