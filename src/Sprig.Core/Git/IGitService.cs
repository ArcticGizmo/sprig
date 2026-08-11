namespace Sprig.Core.Git;

/// <summary>Git operations sprig needs. Backed by shelling out to <c>git</c>; fakeable in tests.</summary>
public interface IGitService
{
    /// <summary>True if <paramref name="path"/> is inside a git working tree.</summary>
    bool IsGitRepo(string path);

    /// <summary>
    /// Relative paths (forward-slash, from the repo root) of every file tracked in the index.
    /// Best-effort: returns empty if <paramref name="repo"/> is not a repo or git fails.
    /// </summary>
    IReadOnlyCollection<string> ListTrackedFiles(string repo);

    /// <summary>
    /// True if <paramref name="relativePath"/> would be excluded by the repo's gitignore rules —
    /// answered from the rules alone (<c>check-ignore --no-index</c>), so it holds even for a path
    /// that doesn't exist yet. Best-effort: false on any error.
    /// </summary>
    bool IsIgnored(string repo, string relativePath);

    /// <summary>Absolute top-level directory of the repo containing <paramref name="path"/>.</summary>
    string ResolveRepoRoot(string path);

    /// <summary>True if a local branch of that name exists.</summary>
    bool BranchExists(string repo, string branch);

    /// <summary>Add a worktree at <paramref name="worktreePath"/> on a new branch off current HEAD.</summary>
    void AddWorktree(string repo, string worktreePath, string branch);

    /// <summary>Fetch from all remotes (with prune). Best-effort: does nothing useful, and does not
    /// throw, when the repo has no remote — a refresh of a purely-local repo still resyncs to its
    /// local base branch.</summary>
    void Fetch(string repo);

    /// <summary>The ref a workspace's repos resync to on a refresh — the remote's default branch when
    /// there is one (<c>origin/HEAD</c> → e.g. <c>origin/main</c>), else the local <c>main</c>/<c>master</c>.
    /// Throws when none can be found.</summary>
    string ResolveDefaultBase(string repo);

    /// <summary>Hard-reset the checked-out branch (and working tree) to <paramref name="reference"/>.
    /// Touches <b>tracked</b> files only — gitignored artifacts (node_modules, build output, real
    /// <c>.env</c> values) are left in place, which is what makes a refresh cheap.</summary>
    void ResetHard(string repo, string reference);

    /// <summary>Number of commits on HEAD that <paramref name="baseRef"/> does not contain — i.e. work a
    /// hard-reset to base would discard. 0 when HEAD is at or behind base. Best-effort: 0 on any error.</summary>
    int CountCommitsAhead(string repo, string baseRef);

    /// <summary>Parse <c>worktree list --porcelain</c> (includes the <c>prunable</c> flag).</summary>
    IReadOnlyList<WorktreeInfo> ListWorktrees(string repo);

    /// <summary>Remove a worktree — always forced, since sprig worktrees carry an untracked .env.</summary>
    void RemoveWorktree(string repo, string worktreePath);

    /// <summary>Prune stale worktree admin entries (folder deleted out from under git).</summary>
    void Prune(string repo);

    /// <summary>Force-delete a local branch (used only on forced teardown).</summary>
    void DeleteBranch(string repo, string branch);
}
