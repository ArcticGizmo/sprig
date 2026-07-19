using System.Text.RegularExpressions;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Store;
using Sprig.Core.Substitution;

namespace Sprig.Core.Workspaces;

/// <summary>Thrown when a workspace operation cannot proceed (bad input, conflict, invalid config).</summary>
public sealed class WorkspaceException(string message) : Exception(message);

/// <summary>
/// Orchestrates the single-repo workspace lifecycle (M2): create → worktree + branch +
/// clobbered .env + record; teardown → layered/idempotent per the S3 matrix. Multi-repo stacks
/// and docker infra are added in M3/M4.
/// </summary>
public sealed partial class WorkspaceService(
    IGitService git,
    IPortStore ports,
    InstanceStore instances,
    EnvClobberService env)
{
    public const string ConfigFileName = ".sprig.json";

    public IReadOnlyList<InstanceRecord> List() => instances.LoadAll();
    public InstanceRecord? Get(string workspace) => instances.TryLoad(workspace);

    /// <summary>Create an isolated workspace from a single repo. Rolls back on failure.</summary>
    public InstanceRecord Create(string repoPath, string workspace)
    {
        ValidateName(workspace);

        if (!git.IsGitRepo(repoPath))
            throw new WorkspaceException($"'{repoPath}' is not a git repository");
        var repoRoot = git.ResolveRepoRoot(repoPath);

        var config = LoadValidConfig(repoRoot);

        if (instances.TryLoad(workspace) is not null)
            throw new WorkspaceException($"workspace '{workspace}' already exists");

        var repoName = Path.GetFileName(repoRoot.TrimEnd('\\', '/'));
        var parent = Directory.GetParent(repoRoot)?.FullName
            ?? throw new WorkspaceException($"repo '{repoRoot}' has no parent directory for a sibling worktree");
        var worktree = Path.Combine(parent, $"{repoName}--{workspace}");
        var branch = $"sprig/{workspace}";

        if (Directory.Exists(worktree))
            throw new WorkspaceException($"worktree path already exists: {worktree}");

        var portsAcquired = false;
        var worktreeAdded = false;
        try
        {
            var portMap = ports.Acquire(workspace, config.Ports.Select(p => p.Name).ToList());
            portsAcquired = true;

            var scope = SprigScope.ForWorkspace(workspace, portMap);

            git.AddWorktree(repoRoot, worktree, branch);
            worktreeAdded = true;

            env.Apply(config, repoRoot, worktree, scope);

            var record = new InstanceRecord
            {
                Workspace = workspace,
                Repos =
                [
                    new InstanceRepo
                    {
                        Name = config.Name,
                        SourcePath = repoRoot,
                        WorktreePath = worktree,
                        Branch = branch,
                    }
                ],
                Ports = new Dictionary<string, int>(portMap),
                LastStatus = "created",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            instances.Save(record);
            return record;
        }
        catch
        {
            // Best-effort rollback so a failed create leaves no mess.
            if (worktreeAdded)
            {
                TryQuiet(() => git.RemoveWorktree(repoRoot, worktree));
                TryQuiet(() => git.DeleteBranch(repoRoot, branch));
            }
            WorktreeInspector.TryDeleteDirectory(worktree);
            if (portsAcquired) TryQuiet(() => ports.Release(workspace));
            TryQuiet(() => instances.Delete(workspace));
            throw;
        }
    }

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
