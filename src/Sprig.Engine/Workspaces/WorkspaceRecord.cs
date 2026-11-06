using Sprig.Engine.Compose;

namespace Sprig.Engine.Workspaces;

public enum WorkspaceState { Starting, Up, Down, Failed }

/// <param name="RepoPath">The repo as the orchestration names it.</param>
/// <param name="Source">The repo's own checkout.</param>
/// <param name="Dir">Where this workspace runs it: a worktree, or <see cref="Source"/> when it runs in place.</param>
/// <param name="BranchCreated">Sprig made the branch for this workspace, rather than checking out one that existed.</param>
/// <param name="From">The branch sprig started the workspace's branch from, when it made it.</param>
public sealed record WorkspaceRepo(string RepoPath, string Name, string Source, string Dir, bool Worktree, bool BranchCreated, string? From = null);

/// <summary>What sprig knows about one workspace: where its repos are, what it wrote, and the compose projects it
/// started. Kept in the store so <c>down</c> and <c>rm</c> undo exactly what <c>up</c> did.</summary>
/// <param name="Branch">Null when the repos run in place.</param>
/// <param name="Folder">The folder holding the workspace's worktrees; null when the repos run in place.</param>
/// <param name="LastUpAt">When it last finished coming up; null for a record from before this was kept.</param>
public sealed record WorkspaceRecord(
    string Orchestration,
    string OrchestrationPath,
    string Name,
    string? Branch,
    string PortPolicy,
    IReadOnlyList<WorkspaceRepo> Repos,
    IReadOnlyList<ComposeProject> Compose,
    IReadOnlyList<WrittenFile> Files,
    WorkspaceState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Folder = null,
    string? Error = null,
    DateTimeOffset? LastUpAt = null)
{
    public bool InPlace => Branch is null;

    /// <summary>Names the workspace in the port leases.</summary>
    public string Key => KeyOf(Orchestration, Name);

    public static string KeyOf(string orchestration, string workspace) => $"{orchestration}/{workspace}";
}
