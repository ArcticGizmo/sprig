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
/// <summary>What a paused workspace had going, so it can be put back as it was.</summary>
/// <param name="Apps">By key (<c>repo/app</c>); jobs aren't kept, since the apps run them again as they start.</param>
/// <param name="Infra">By <see cref="InfraTarget.Key"/>, every container that was running, used by an app or not.</param>
public sealed record PausedRun(IReadOnlyList<string> Apps, IReadOnlyList<string> Infra, DateTimeOffset At);

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
/// <param name="CheckoutBranch">For one that runs in place, the branch the checkout was on when it last came up; the
/// checkout is the user's own, so sprig never switches it.</param>
/// <param name="ReadyFrom">Every file it was last made ready from (definitions and compose files), with a hash of each
/// one's content then; null for a record from before this was kept.</param>
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
    IReadOnlyDictionary<string, string>? Values = null,
    string? CheckoutBranch = null,
    IReadOnlyDictionary<string, string>? ReadyFrom = null)
{
    /// <summary>The workspace every repo and orchestration has that runs its repos where they're checked out.</summary>
    public const string MainName = "main";

    /// <summary>The agent session a worktree hook made this slot for, so slots an agent never gave back can be found and
    /// freed; null for one a person made.</summary>
    public string? MadeForAgent { get; init; }

    /// <summary>What was up when it was paused, for Resume to start again; null when it isn't paused.</summary>
    public PausedRun? Paused { get; init; }

    public bool InPlace => Branch is null && Folder is null;

    /// <summary>The repos' own checkouts, run as they are: on their own ports, with nothing written into them. It's always there, so it can't be removed,
    /// and only one definition runs in a checkout at a time.</summary>
    public bool IsMain => InPlace && Name == MainName;

    /// <summary>A slot with nothing checked out, ready for the next branch.</summary>
    public bool IsFree => State == WorkspaceState.Released;

    /// <summary>What git and the file system call it: the slot's folder name, e.g. <c>storefront--2</c>; for main, the ID
    /// those slots are numbered from; else the workspace's name when it runs in place.</summary>
    public string SlotName => IsMain ? Orchestration : InPlace ? Name : $"{Orchestration}--{Name}";

    /// <summary>How people know it: its branch, or that it's free.</summary>
    public string Label => Branch ?? (IsFree ? "free" : InPlace ? CheckoutBranch ?? Name : Name);

    /// <summary>Names the workspace in the port leases.</summary>
    public string Key => KeyOf(Orchestration, Name);

    public static string KeyOf(string orchestration, string workspace) => $"{orchestration}/{workspace}";
}
