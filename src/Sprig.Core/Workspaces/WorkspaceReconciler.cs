using Sprig.Core.Git;
using Sprig.Core.Store;

namespace Sprig.Core.Workspaces;

/// <summary>The reconciliation state of one repo within a workspace.</summary>
public sealed record RepoReconcileState(string RepoName, string SourcePath, string WorktreePath, WorktreeState State);

/// <summary>A workspace's drift report: per-repo states vs. what the instance record expects.</summary>
public sealed record WorkspaceReconcile(string Workspace, IReadOnlyList<RepoReconcileState> Repos)
{
    public bool IsHealthy => Repos.All(r => r.State == WorktreeState.Healthy);
    public bool HasDrift => Repos.Any(r => r.State is WorktreeState.MissingFolder or WorktreeState.Orphaned);
}

/// <summary>
/// Detects and repairs record-vs-reality drift (the objective's safety promise). Repair applies
/// the S3 matrix: prune stale registrations (folder deleted) and remove sprig-owned orphan
/// folders (git no longer tracks them). It never touches Healthy worktrees.
/// </summary>
public sealed class WorkspaceReconciler(IGitService git, InstanceStore instances)
{
    /// <summary>Classify one workspace, or <c>null</c> if it has no record.</summary>
    public WorkspaceReconcile? Inspect(string workspace)
    {
        var record = instances.TryLoad(workspace);
        if (record is null) return null;

        var repos = record.Repos
            .Select(r => new RepoReconcileState(
                r.Name, r.SourcePath, r.WorktreePath,
                WorktreeInspector.Classify(git, r.SourcePath, r.WorktreePath)))
            .ToList();

        return new WorkspaceReconcile(workspace, repos);
    }

    /// <summary>Classify every workspace in the store (the <c>doctor</c> sweep).</summary>
    public IReadOnlyList<WorkspaceReconcile> InspectAll()
        => instances.LoadAll()
            .Select(r => Inspect(r.Workspace))
            .OfType<WorkspaceReconcile>()
            .ToList();

    /// <summary>Repair drift for one workspace; returns a human-readable list of actions taken.</summary>
    public IReadOnlyList<string> Repair(string workspace)
    {
        var report = Inspect(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");

        var actions = new List<string>();
        foreach (var repo in report.Repos)
        {
            var isRepo = git.IsGitRepo(repo.SourcePath);
            switch (repo.State)
            {
                case WorktreeState.MissingFolder when isRepo:
                    git.Prune(repo.SourcePath);
                    actions.Add($"pruned stale worktree registration: {repo.WorktreePath}");
                    break;
                case WorktreeState.Orphaned:
                    WorktreeInspector.TryDeleteDirectory(repo.WorktreePath);
                    if (isRepo) git.Prune(repo.SourcePath);
                    actions.Add($"removed orphan worktree folder: {repo.WorktreePath}");
                    break;
                case WorktreeState.Healthy:
                case WorktreeState.Gone:
                default:
                    break;
            }
        }
        return actions;
    }
}
