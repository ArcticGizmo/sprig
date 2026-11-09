using Sprig.Engine.Compose;

namespace Sprig.Engine.Workspaces;

/// <summary><see cref="Released"/>: a slot whose worktrees are on a detached HEAD, free for the next branch, with its
/// ports, values, data and build output kept for it.</summary>
public enum WorkspaceState { Starting, Up, Down, Failed, Released }

/// <param name="RepoPath">The repo as the orchestration names it.</param>
/// <param name="Source">The repo's own checkout.</param>
/// <param name="Dir">Where this workspace runs it: a worktree, or <see cref="Source"/> when it runs in place.</param>
/// <param name="BranchCreated">Sprig made the branch for this workspace, rather than checking out one that existed.</param>
/// <param name="From">The branch sprig started the workspace's branch from, when it made it.</param>
public sealed record WorkspaceRepo(string RepoPath, string Name, string Source, string Dir, bool Worktree, bool BranchCreated, string? From = null);

/// <summary>What sprig knows about one workspace: where its repos are, what it wrote, and the compose projects it
/// started. Kept in the store so <c>down</c> and <c>rm</c> undo exactly what <c>up</c> did.</summary>
/// <param name="Name">A slot's number ("1", "2", ...) for a workspace of worktrees; a name such as <c>local</c> for one
/// that runs the repos in place.</param>
/// <param name="Branch">The branch its worktrees have checked out, which is how people know it. Null when the repos run in
/// place, or the slot is free.</param>
/// <param name="Folder">The folder holding the workspace's worktrees (for a repo on its own, the worktree itself); null
/// when the repos run in place.</param>
/// <param name="LastUpAt">When it last finished coming up; null for a record from before this was kept.</param>
/// <param name="Values">Values set on this workspace, by setting name. They win over the orchestration's and the repos'
/// defaults.</param>
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
    DateTimeOffset? LastUpAt = null,
    IReadOnlyDictionary<string, string>? Values = null)
{
    public bool InPlace => Branch is null && Folder is null;

    /// <summary>A slot with nothing checked out, ready for the next branch.</summary>
    public bool IsFree => State == WorkspaceState.Released;

    /// <summary>What git and the file system call it: the slot's folder name, e.g. <c>storefront--2</c>; the workspace's
    /// name when it runs in place.</summary>
    public string SlotName => InPlace ? Name : $"{Orchestration}--{Name}";

    /// <summary>How people know it: its branch, or that it's free.</summary>
    public string Label => Branch ?? (IsFree ? "free" : Name);

    /// <summary>Names the workspace in the port leases.</summary>
    public string Key => KeyOf(Orchestration, Name);

    public static string KeyOf(string orchestration, string workspace) => $"{orchestration}/{workspace}";
}
