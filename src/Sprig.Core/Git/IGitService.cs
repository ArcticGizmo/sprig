namespace Sprig.Core.Git;

/// <summary>Git operations sprig needs. Backed by shelling out to <c>git</c>; fakeable in tests.</summary>
public interface IGitService
{
    /// <summary>True if <paramref name="path"/> is inside a git working tree.</summary>
    bool IsGitRepo(string path);

    /// <summary>Absolute top-level directory of the repo containing <paramref name="path"/>.</summary>
    string ResolveRepoRoot(string path);

    /// <summary>True if a local branch of that name exists.</summary>
    bool BranchExists(string repo, string branch);

    /// <summary>Add a worktree at <paramref name="worktreePath"/> on a new branch off current HEAD.</summary>
    void AddWorktree(string repo, string worktreePath, string branch);

    /// <summary>Parse <c>worktree list --porcelain</c> (includes the <c>prunable</c> flag).</summary>
    IReadOnlyList<WorktreeInfo> ListWorktrees(string repo);

    /// <summary>Remove a worktree — always forced, since sprig worktrees carry an untracked .env.</summary>
    void RemoveWorktree(string repo, string worktreePath);

    /// <summary>Prune stale worktree admin entries (folder deleted out from under git).</summary>
    void Prune(string repo);

    /// <summary>Force-delete a local branch (used only on forced teardown).</summary>
    void DeleteBranch(string repo, string branch);
}
