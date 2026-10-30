using Sprig.Core.Git;
using Sprig.Core.Store;

namespace Sprig.Core.Workspaces;

/// <summary>The state of a worktree's sibling link (<see cref="InstanceRepo.LinkPath"/>) vs. what the record expects.</summary>
public enum LinkState
{
    /// <summary>The worktree has no link (sibling layout).</summary>
    None,
    /// <summary>The link is there and points at the worktree.</summary>
    Ok,
    /// <summary>The worktree is there but its link is not (→ recreate).</summary>
    Missing,
    /// <summary>A link is there but points somewhere else (→ re-point).</summary>
    WrongTarget,
    /// <summary>The link is there but its worktree folder is not (→ remove the link).</summary>
    Dangling,
    /// <summary>Something that isn't a link occupies the link path. Never touched; the user has to move it.</summary>
    Blocked,
}

/// <summary>The reconciliation state of one repo within a workspace.</summary>
public sealed record RepoReconcileState(string RepoName, string SourcePath, string WorktreePath, WorktreeState State,
    string? LinkPath = null, LinkState Link = LinkState.None);

/// <summary>A workspace's drift report: per-repo states vs. what the instance record expects.</summary>
public sealed record WorkspaceReconcile(string Workspace, IReadOnlyList<RepoReconcileState> Repos)
{
    public bool IsHealthy => Repos.All(r => r.State == WorktreeState.Healthy && r.Link is LinkState.None or LinkState.Ok);
    public bool HasDrift => Repos.Any(r => r.State is WorktreeState.MissingFolder or WorktreeState.Orphaned
                                           || r.Link is not (LinkState.None or LinkState.Ok));
}

/// <summary>
/// Detects and repairs record-vs-reality drift (the objective's safety promise). Repair applies
/// the S3 matrix: prune stale registrations (folder deleted) and remove sprig-owned orphan
/// folders (git no longer tracks them). It never touches Healthy worktrees, and only ever
/// creates or removes links — never whatever else sits at a link path.
/// </summary>
public sealed class WorkspaceReconciler(IGitService git, InstanceStore instances)
{
    /// <summary>Classify one workspace, or <c>null</c> if it has no record.</summary>
    public WorkspaceReconcile? Inspect(string workspace)
    {
        var record = instances.TryLoad(workspace);
        if (record is null) return null;

        var repos = record.Repos
            .Select(r =>
            {
                var state = WorktreeInspector.Classify(git, r.SourcePath, r.WorktreePath);
                return new RepoReconcileState(r.Name, r.SourcePath, r.WorktreePath, state,
                    r.LinkPath, ClassifyLink(r.LinkPath, r.WorktreePath));
            })
            .ToList();

        return new WorkspaceReconcile(workspace, repos);
    }

    static LinkState ClassifyLink(string? link, string worktree)
    {
        if (link is null) return LinkState.None;
        var worktreeThere = Directory.Exists(worktree);
        if (!DirectoryLink.Occupied(link)) return worktreeThere ? LinkState.Missing : LinkState.None;
        if (!DirectoryLink.IsLink(link)) return LinkState.Blocked;
        if (!worktreeThere) return LinkState.Dangling;
        return DirectoryLink.PointsTo(link, worktree) ? LinkState.Ok : LinkState.WrongTarget;
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
                    // The link would otherwise point into a folder that is about to go.
                    if (repo.LinkPath is not null) DirectoryLink.Remove(repo.LinkPath);
                    WorktreeInspector.TryDeleteDirectory(repo.WorktreePath);
                    if (isRepo) git.Prune(repo.SourcePath);
                    actions.Add($"removed orphan worktree folder: {repo.WorktreePath}");
                    break;
                case WorktreeState.Healthy:
                case WorktreeState.Gone:
                default:
                    break;
            }

            if (repo.LinkPath is not { } link) continue;
            switch (repo.Link)
            {
                case LinkState.Missing when repo.State == WorktreeState.Healthy:
                    DirectoryLink.Create(link, repo.WorktreePath);
                    actions.Add($"recreated worktree shortcut: {link}");
                    break;
                case LinkState.WrongTarget when repo.State == WorktreeState.Healthy:
                    DirectoryLink.Remove(link);
                    DirectoryLink.Create(link, repo.WorktreePath);
                    actions.Add($"re-pointed worktree shortcut: {link}");
                    break;
                case LinkState.Dangling:
                    DirectoryLink.Remove(link);
                    actions.Add($"removed shortcut to a missing worktree: {link}");
                    break;
                case LinkState.Blocked:
                    actions.Add($"left {link} alone: it isn't a link, so the worktree shortcut can't be restored until it's moved");
                    break;
            }
        }
        return actions;
    }
}
