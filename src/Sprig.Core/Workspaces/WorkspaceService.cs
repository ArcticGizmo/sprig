using System.Text.RegularExpressions;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Docker;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Planning;
using Sprig.Core.Ports;
using Sprig.Core.Shared;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

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
    Setup.SetupRunner? setup = null,
    Shared.SharedResourceStore? shared = null)
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

    /// <summary>
    /// Build the plan for a workspace, with every enabled shared-resource overlay applied unless
    /// <paramref name="options"/> opts out. One method, so create, the create checklist, and
    /// <c>sprig plan</c> can never disagree about what is going to happen.
    /// </summary>
    WorkspacePlan BuildPlan(ResolvedStack stack, string workspace, CreateOptions? options)
    {
        var plan = WorkspacePlanner.Plan(stack, workspace);
        if (options?.NoShared == true || shared is null) return plan;
        return OverlayEngine.Apply(plan, shared.Active());
    }

    /// <summary>Create an isolated workspace from a single ad-hoc repo. Rolls back on failure.</summary>
    public InstanceRecord Create(string repoPath, string workspace,
        IProgress<WorkspaceStepProgress>? progress = null)
        => Create(ResolveSingleRepo(repoPath), workspace, progress);

    /// <summary>The ordered checklist <see cref="Create(ResolvedStack, string, IProgress{WorkspaceStepProgress})"/>
    /// will work through, computed up front so a UI can show every row before execution starts. Runs the
    /// same cheap pre-flight validation as create, so a bad name / duplicate workspace fails here rather
    /// than mid-checklist.</summary>
    public IReadOnlyList<WorkspaceStep> PlanCreate(ResolvedStack stack, string workspace,
        CreateOptions? options = null)
    {
        ValidateCreate(stack, workspace);
        // Derive the checklist from the plan, not the raw stack, so the rows a UI pre-renders keep
        // matching what create actually does once a layer above the stack can add or remove work.
        var plan = BuildPlan(stack, workspace, options);
        var steps = new List<WorkspaceStep> { new(CreateStepIds.Ports, "Allocate ports") };
        foreach (var repo in plan.EffectiveRepos)
        {
            steps.Add(new(CreateStepIds.Worktree(repo.Name), $"Create worktree — {repo.Name}"));
            steps.Add(new(CreateStepIds.Env(repo.Name), $"Apply environment — {repo.Name}"));
            if (repo.Config.Compose.Count > 0)
                steps.Add(new(CreateStepIds.Compose(repo.Name), $"Generate compose — {repo.Name}"));
            if (HasSetup(repo))
            {
                // A parent "Install dependencies" row with one indented sub-row per command, so each
                // command's progress (and live output) is visible on its own line.
                steps.Add(new(CreateStepIds.Setup(repo.Name), $"Install dependencies — {repo.Name}"));
                for (var i = 0; i < repo.Config.Setup.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(repo.Config.Setup[i])) continue;
                    steps.Add(new(CreateStepIds.SetupCommand(repo.Name, i), repo.Config.Setup[i]) { SubStep = true });
                }
            }
        }
        steps.Add(new(CreateStepIds.Record, "Save workspace record"));
        return steps;
    }

    /// <summary>Whether this repo has setup commands to run (and a runner to run them).</summary>
    bool HasSetup(ResolvedRepo repo) => setup is not null && repo.Config.Setup.Count > 0;

    /// <summary>The services a shared resource provides for one of this repo's compose files.</summary>
    static IReadOnlyList<string> SuppressedIn(BoundRepo repo, string file)
        => [.. repo.Suppress.Where(s => SamePath(s.File, file)).Select(s => s.Service)];

    // '\' and '/' reach the same file, and './x' is 'x' — two spellings must not read as two files.
    static bool SamePath(string a, string b)
        => string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    static string Trim(string file)
    {
        var path = file.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path.TrimStart('/');
    }

    /// <summary>Run the repo's setup commands one at a time, reporting each as its own sub-step and
    /// streaming its live output to the progress sink. Stops at the first failure (a later command
    /// usually depends on an earlier one); the failed command's row goes Warning — setup never rolls
    /// the workspace back — and any commands after it stay Pending (unreached).</summary>
    IReadOnlyList<Setup.SetupOutcome> RunSetup(ResolvedRepo repo, string worktree,
        IProgress<WorkspaceStepProgress>? progress)
    {
        var outcomes = new List<Setup.SetupOutcome>();
        for (var i = 0; i < repo.Config.Setup.Count; i++)
        {
            var command = repo.Config.Setup[i];
            if (string.IsNullOrWhiteSpace(command)) continue;

            var id = CreateStepIds.SetupCommand(repo.Name, i);
            progress?.Report(new(id, WorkspaceStepState.Running));
            var outcome = setup!.RunCommand(command, worktree,
                onOutput: line => progress?.Report(new(id, WorkspaceStepState.Running) { Output = line }));
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
    /// <see cref="PlanCreate"/>).</summary>
    public InstanceRecord Create(ResolvedStack stack, string workspace,
        IProgress<WorkspaceStepProgress>? progress = null, CreateOptions? options = null)
    {
        ValidateCreate(stack, workspace);

        var branch = $"sprig/{workspace}";

        // Stage 1: what we intend to do, before a single port is reserved. Hard-fails on an unbound
        // input or an overlay whose target has moved, so a stack that can't produce a workspace says
        // so before touching the filesystem.
        var plan = BuildPlan(stack, workspace, options);

        // Pre-compute each repo's sibling worktree path and guard against collisions.
        var plans = new List<RepoPlan>();
        foreach (var repo in plan.EffectiveRepos)
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
            // The stack owns the ports; allocate one real non-colliding number per named port that the
            // plan still references — a port nothing points at is not reserved. A repo input may pin its
            // port to a fixed set (e.g. pre-registered Auth0 callbacks); resolve those onto the stack
            // ports so allocation only draws from the allowed set.
            progress?.Report(new(CreateStepIds.Ports, WorkspaceStepState.Running));
            var constraints = PortConstraintResolver.Resolve(plan);
            var requests = plan.ReferencedPorts
                .Select(p => new PortRequest(p, constraints.GetValueOrDefault(p)))
                .ToList();
            var allPorts = ports.Acquire(workspace, requests);
            portsAcquired = true;

            // Stage 2: feed the allocated numbers back in and resolve every expression.
            var bound = WorkspacePlanner.Bind(plan, allPorts);
            var boundByName = bound.Repos.ToDictionary(r => r.Name, StringComparer.Ordinal);
            progress?.Report(new(CreateStepIds.Ports, WorkspaceStepState.Done));

            var repoRecords = new List<InstanceRepo>();
            foreach (var repoPlan in plans)
            {
                var repo = repoPlan.Repo;
                var boundRepo = boundByName[repo.Name];
                var repoScope = boundRepo.Scope;

                current = CreateStepIds.Worktree(repo.Name);
                progress?.Report(new(current, WorkspaceStepState.Running));
                git.AddWorktree(repo.Root, repoPlan.Worktree, branch);
                addedWorktrees.Add((repo.Root, repoPlan.Worktree));
                progress?.Report(new(current, WorkspaceStepState.Done));

                current = CreateStepIds.Env(repo.Name);
                progress?.Report(new(current, WorkspaceStepState.Running));
                env.Apply(repo.Config, repo.Root, repoPlan.Worktree, repoScope);
                progress?.Report(new(current, WorkspaceStepState.Done));

                // A repo may override several compose files; generate one isolated copy per file,
                // named so files from different source paths never collide in the instance dir.
                var composePaths = new List<string>();
                if (repo.Config.Compose.Count > 0)
                {
                    current = CreateStepIds.Compose(repo.Name);
                    progress?.Report(new(current, WorkspaceStepState.Running));
                    foreach (var composeCfg in repo.Config.Compose)
                    {
                        var dest = Path.Combine(paths.InstanceDir(workspace),
                            $"docker-compose.{repo.Name}.{ComposeSlug(composeCfg.File)}.sprig.yml");
                        var suppressed = SuppressedIn(boundRepo, composeCfg.File);
                        // Null means every service in the file was provided by a shared resource, so
                        // there is no file to write and nothing for this workspace to bring up.
                        if (compose.GenerateToFile(Path.Combine(repo.Root, composeCfg.File), composeCfg,
                                repoScope, dest, suppressed) is { } written)
                            composePaths.Add(written);
                    }
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
                    setupOutcomes = RunSetup(repo, repoPlan.Worktree, progress);
                    var failed = setupOutcomes.FirstOrDefault(o => !o.Success);
                    progress?.Report(failed is null
                        ? new(setupStep, WorkspaceStepState.Done)
                        : new(setupStep, WorkspaceStepState.Warning, $"'{failed.Command}' exited {failed.ExitCode} — worktree kept"));
                }

                repoRecords.Add(new InstanceRepo
                {
                    Name = repo.Name,
                    SourcePath = repo.Root,
                    WorktreePath = repoPlan.Worktree,
                    Branch = branch,
                    GeneratedComposePaths = composePaths,
                    Inputs = boundRepo.Inputs,
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
    }

    /// <summary>
    /// Dry-run the plan for a workspace that doesn't exist yet. Nothing is allocated and nothing is
    /// written, so ports render as <c>{name}</c> placeholders. Hard-fails on an unbound input, which
    /// makes this the cheapest way to find out whether a stack can actually produce a workspace.
    /// </summary>
    public BoundPlan PreviewPlan(ResolvedStack stack, string workspace, CreateOptions? options = null)
        => WorkspacePlanner.Preview(BuildPlan(stack, workspace, options));

    /// <summary>
    /// Re-plan an existing workspace against the ports it actually holds — the "why is this value what it
    /// is?" view for something already on disk.
    /// </summary>
    public BoundPlan ExplainPlan(ResolvedStack stack, string workspace, CreateOptions? options = null)
    {
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        return WorkspacePlanner.Bind(BuildPlan(stack, workspace, options), record.Ports);
    }

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
    /// being gone. The branch is deleted only when <paramref name="force"/> is set; the record
    /// is removed last so an interrupted teardown is resumable. Reports checklist progress to
    /// <paramref name="progress"/> if supplied (steps match <see cref="PlanRemove"/>); because
    /// teardown is best-effort, a step whose action throws is reported as a Warning, not an Error —
    /// the sweep always runs to completion.
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

        // Step 1 of the S3 matrix: infra down (and wipe volumes) before touching worktrees.
        var dockerUp = docker.IsAvailable();
        foreach (var repo in record.Repos.Where(r => r.ComposePaths.Count > 0))
        {
            var id = RemoveStepIds.Infra(repo.Name);
            if (!dockerUp)
            {
                progress?.Report(new(id, WorkspaceStepState.Warning, "Docker unavailable — containers not stopped"));
                continue;
            }
            Step(progress, id, () =>
                docker.Down(repo.ComposePaths, repo.WorktreePath, ProjectName(workspace), removeVolumes: true));
        }

        foreach (var repo in record.Repos)
        {
            var isRepo = git.IsGitRepo(repo.SourcePath);

            Step(progress, RemoveStepIds.Worktree(repo.Name), () =>
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
                Step(progress, RemoveStepIds.Branch(repo.Name), () =>
                {
                    if (isRepo && git.BranchExists(repo.SourcePath, repo.Branch))
                        git.DeleteBranch(repo.SourcePath, repo.Branch);
                });
        }

        Step(progress, RemoveStepIds.Ports, () => ports.Release(workspace));
        Step(progress, RemoveStepIds.Record, () => instances.Delete(workspace));
    }

    /// <summary>Run one best-effort teardown step, reporting Running → Done, or Warning if it throws
    /// (teardown never aborts on a single failed layer — the exception is surfaced, not propagated).</summary>
    static void Step(IProgress<WorkspaceStepProgress>? progress, string id, Action action)
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
    public void Reset(string workspace)
    {
        Down(workspace);
        Up(workspace);
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
