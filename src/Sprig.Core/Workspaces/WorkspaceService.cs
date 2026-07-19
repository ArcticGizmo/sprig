using System.Text.RegularExpressions;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Docker;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
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
    ISprigPaths paths)
{
    public const string ConfigFileName = ".sprig.json";
    public const string GeneratedComposeName = "docker-compose.sprig.yml";

    static string ProjectName(string workspace) => $"sprig-{workspace}";

    public IReadOnlyList<InstanceRecord> List() => instances.LoadAll();
    public InstanceRecord? Get(string workspace) => instances.TryLoad(workspace);

    /// <summary>Create an isolated workspace from a single ad-hoc repo. Rolls back on failure.</summary>
    public InstanceRecord Create(string repoPath, string workspace)
        => Create(ResolveSingleRepo(repoPath), workspace);

    /// <summary>Create an isolated workspace from a resolved stack (1+ repos). Rolls back on failure.</summary>
    public InstanceRecord Create(ResolvedStack stack, string workspace)
    {
        ValidateName(workspace);
        if (stack.Repos.Count == 0)
            throw new WorkspaceException("nothing to create: the stack has no repos");
        if (instances.TryLoad(workspace) is not null)
            throw new WorkspaceException($"workspace '{workspace}' already exists");

        var branch = $"sprig/{workspace}";

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
        try
        {
            // Allocate all ports at once under namespaced keys "<repo>.<port>".
            var namespaced = plans
                .SelectMany(p => p.Repo.Config.Ports.Select(port => $"{p.Repo.Name}.{port.Name}"))
                .ToList();
            var allPorts = ports.Acquire(workspace, namespaced);
            portsAcquired = true;

            // Split back into per-repo local maps for scope building.
            var portsByRepo = plans.ToDictionary(
                p => p.Repo.Name,
                p => (IReadOnlyDictionary<string, int>)p.Repo.Config.Ports.ToDictionary(
                    port => port.Name, port => allPorts[$"{p.Repo.Name}.{port.Name}"]));

            var scope = StackScopeBuilder.Build(
                workspace, plans.Select(p => (p.Repo.Name, p.Repo.Config)).ToList(), portsByRepo, stack.Vars);

            var repoRecords = new List<InstanceRepo>();
            foreach (var plan in plans)
            {
                var repo = plan.Repo;
                var repoScope = scope.For(repo.Name);

                git.AddWorktree(repo.Root, plan.Worktree, branch);
                addedWorktrees.Add((repo.Root, plan.Worktree));

                env.Apply(repo.Config, repo.Root, plan.Worktree, repoScope);

                string? composePath = null;
                if (repo.Config.Compose is { } composeCfg)
                {
                    composePath = Path.Combine(paths.InstanceDir(workspace), $"docker-compose.{repo.Name}.sprig.yml");
                    compose.GenerateToFile(Path.Combine(repo.Root, composeCfg.File), composeCfg, repoScope, composePath);
                }

                repoRecords.Add(new InstanceRepo
                {
                    Name = repo.Name,
                    SourcePath = repo.Root,
                    WorktreePath = plan.Worktree,
                    Branch = branch,
                    GeneratedComposePath = composePath,
                    Ports = portsByRepo[repo.Name],
                });
            }

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
            return record;
        }
        catch
        {
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

    /// <summary>Resolve an ad-hoc single repo path into a one-repo stack.</summary>
    public ResolvedStack ResolveSingleRepo(string repoPath)
    {
        if (!git.IsGitRepo(repoPath))
            throw new WorkspaceException($"'{repoPath}' is not a git repository");
        var root = git.ResolveRepoRoot(repoPath);
        var config = LoadValidConfig(root);
        return new ResolvedStack(null, [new ResolvedRepo(config.Name, root, config)], new Dictionary<string, string>());
    }

    sealed record RepoPlan(ResolvedRepo Repo, string Worktree);

    /// <summary>
    /// Tear down a workspace. Layered and idempotent: each step tolerates its target already
    /// being gone. The branch is deleted only when <paramref name="force"/> is set; the record
    /// is removed last so an interrupted teardown is resumable.
    /// </summary>
    public void Remove(string workspace, bool force = false)
    {
        var record = instances.TryLoad(workspace);
        if (record is null)
        {
            // No record — still release any stray port lease so nothing leaks.
            TryQuiet(() => ports.Release(workspace));
            return;
        }

        // Step 1 of the S3 matrix: infra down (and wipe volumes) before touching worktrees.
        if (docker.IsAvailable())
        {
            foreach (var repo in record.Repos.Where(r => r.GeneratedComposePath is not null))
                TryQuiet(() => docker.Down(repo.GeneratedComposePath!, repo.WorktreePath, ProjectName(workspace), removeVolumes: true));
        }

        foreach (var repo in record.Repos)
        {
            var isRepo = git.IsGitRepo(repo.SourcePath);
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

            if (force && repo.Branch is not null && isRepo && git.BranchExists(repo.SourcePath, repo.Branch))
                TryQuiet(() => git.DeleteBranch(repo.SourcePath, repo.Branch));
        }

        TryQuiet(() => ports.Release(workspace));
        instances.Delete(workspace);
    }

    /// <summary>Bring the workspace's infra up.</summary>
    public void Up(string workspace)
    {
        var record = RequireWithInfra(workspace, out var infraRepos);
        foreach (var repo in infraRepos)
            docker.Up(repo.GeneratedComposePath!, repo.WorktreePath, ProjectName(workspace));
        instances.Save(record with { LastStatus = "running" });
    }

    /// <summary>Stop the workspace's infra; <paramref name="removeVolumes"/> wipes data.</summary>
    public void Down(string workspace, bool removeVolumes = false)
    {
        var record = RequireWithInfra(workspace, out var infraRepos);
        foreach (var repo in infraRepos)
            docker.Down(repo.GeneratedComposePath!, repo.WorktreePath, ProjectName(workspace), removeVolumes);
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
            .SelectMany(r => docker.Ps(r.GeneratedComposePath!, r.WorktreePath, ProjectName(workspace)))
            .ToList();
    }

    InstanceRecord RequireWithInfra(string workspace, out IReadOnlyList<InstanceRepo> infraRepos)
    {
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        infraRepos = record.Repos.Where(r => r.GeneratedComposePath is not null).ToList();
        if (infraRepos.Count == 0)
            throw new WorkspaceException($"workspace '{workspace}' has no docker infrastructure");
        if (!docker.IsAvailable())
            throw new WorkspaceException("docker compose is not available on this machine");
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
