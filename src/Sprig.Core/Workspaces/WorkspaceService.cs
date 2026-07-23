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
            // The stack owns the ports; allocate one real non-colliding number per named port.
            // A repo input may pin its port to a fixed set (e.g. pre-registered Auth0 callbacks);
            // resolve those onto the stack ports so allocation only draws from the allowed set.
            var constraints = PortConstraintResolver.Resolve(stack.Repos, stack.Bindings, stack.Ports);
            var requests = stack.Ports
                .Select(p => new PortRequest(p, constraints.GetValueOrDefault(p)))
                .ToList();
            var allPorts = ports.Acquire(workspace, requests);
            portsAcquired = true;

            // Resolve per-repo input scopes from the stack's bindings (hard-fails on an unbound input).
            var wired = StackWiring.Resolve(workspace, allPorts, stack.Repos, stack.Bindings);

            var repoRecords = new List<InstanceRepo>();
            foreach (var plan in plans)
            {
                var repo = plan.Repo;
                var repoScope = wired.ScopeFor(repo.Name);

                git.AddWorktree(repo.Root, plan.Worktree, branch);
                addedWorktrees.Add((repo.Root, plan.Worktree));

                env.Apply(repo.Config, repo.Root, plan.Worktree, repoScope);

                // A repo may override several compose files; generate one isolated copy per file,
                // named so files from different source paths never collide in the instance dir.
                var composePaths = new List<string>();
                foreach (var composeCfg in repo.Config.Compose)
                {
                    var dest = Path.Combine(paths.InstanceDir(workspace),
                        $"docker-compose.{repo.Name}.{ComposeSlug(composeCfg.File)}.sprig.yml");
                    compose.GenerateToFile(Path.Combine(repo.Root, composeCfg.File), composeCfg, repoScope, dest);
                    composePaths.Add(dest);
                }

                repoRecords.Add(new InstanceRepo
                {
                    Name = repo.Name,
                    SourcePath = repo.Root,
                    WorktreePath = plan.Worktree,
                    Branch = branch,
                    GeneratedComposePaths = composePaths,
                    Inputs = wired.Inputs[repo.Name],
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
        // Ad-hoc single repo: no stack, so only zero-input repos can stand up this way.
        return new ResolvedStack(null, [new ResolvedRepo(config.Name, root, config)],
            [], new Dictionary<string, IReadOnlyDictionary<string, string>>());
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
            foreach (var repo in record.Repos.Where(r => r.ComposePaths.Count > 0))
                TryQuiet(() => docker.Down(repo.ComposePaths, repo.WorktreePath, ProjectName(workspace), removeVolumes: true));
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
