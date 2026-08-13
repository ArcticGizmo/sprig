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

    /// <summary>True if a remote-tracking branch <c>&lt;remote&gt;/&lt;branch&gt;</c> exists locally (any remote).
    /// A heads-up on claim (the name is taken upstream), never a hard block.</summary>
    bool RemoteBranchExists(string repo, string branch);

    /// <summary>True if <paramref name="name"/> is a valid git branch name
    /// (<c>git check-ref-format --branch</c>) — stricter than a charset regex: rejects leading <c>-</c>,
    /// trailing <c>.lock</c>, <c>..</c>, and the rest of git's ref rules.</summary>
    bool IsValidBranchName(string name);

    /// <summary>Add a worktree at <paramref name="worktreePath"/> on a new branch off current HEAD.</summary>
    void AddWorktree(string repo, string worktreePath, string branch);

    /// <summary>Add a worktree at <paramref name="worktreePath"/> in <b>detached HEAD</b> at
    /// <paramref name="reference"/> — a parked workspace with no branch of its own, so any number of workspaces can
    /// share the same base commit (git forbids the same branch in two worktrees; detached sidesteps it).</summary>
    void AddWorktreeDetached(string repo, string worktreePath, string reference);

    /// <summary>In the worktree, create and switch to a new branch. When <paramref name="startPoint"/> is
    /// given the branch starts there; otherwise it starts at the worktree's current (detached) HEAD.</summary>
    void SwitchNewBranch(string worktreePath, string branch, string? startPoint = null);

    /// <summary>Detach the worktree's HEAD to <paramref name="reference"/> — park it, leaving no branch
    /// checked out. The branch it was on (if any) is left as a ref, not deleted.</summary>
    void DetachTo(string worktreePath, string reference);

    /// <summary>True if the worktree has uncommitted changes — staged, unstaged, or untracked
    /// (<c>git status --porcelain</c> non-empty).</summary>
    bool HasUncommittedChanges(string worktreePath);

    /// <summary>Commits reachable from the worktree's HEAD that no remote-tracking branch contains — work
    /// that would be stranded if the branch ref were later reset. 0 when HEAD is already on a remote. A repo
    /// with no remote reports its whole history as unpushed (nothing to compare against), which is honest.</summary>
    int CountUnpushedCommits(string worktreePath);

    /// <summary>Fetch from all remotes (with prune). Best-effort: does nothing useful, and does not
    /// throw, when the repo has no remote — a refresh of a purely-local repo still resyncs to its
    /// local base branch.</summary>
    void Fetch(string repo);

    /// <summary>The default ref a workspace branches from / resyncs to. Prefers an <c>upstream</c> remote
    /// over <c>origin</c> (fork/gitflow: you branch from the canonical repo, not your fork's stale main):
    /// <c>&lt;remote&gt;/HEAD</c> → e.g. <c>upstream/main</c>, else <c>&lt;remote&gt;/main|master</c>, else a
    /// local <c>main</c>/<c>master</c>. Throws when none can be found.</summary>
    string ResolveDefaultBase(string repo);

    /// <summary>Candidate start points for the "start from" picker: every remote-tracking branch (all
    /// remotes) and every local branch, each with its tip-commit date (for recency ordering), minus the
    /// symbolic <c>&lt;remote&gt;/HEAD</c> entries. Best-effort: empty on any error. Fetch first if you want
    /// them current.</summary>
    IReadOnlyList<BranchRef> ListStartPointCandidates(string repo);

    /// <summary>The repo's currently checked-out branch (short name), or null when HEAD is detached — so a
    /// picker can flag "branch from where you are now".</summary>
    string? CurrentBranch(string repo);

    /// <summary>Recent commits across every ref (<c>git log --all --date-order</c>, newest first, capped at
    /// <paramref name="limit"/>) with parents and ref decorations — the input to the branch-graph view.
    /// Best-effort: empty on any error.</summary>
    IReadOnlyList<GraphCommit> ListCommitGraph(string repo, int limit);

    /// <summary>True if <paramref name="reference"/> resolves to a commit in the repo — used to check a
    /// chosen start point exists before branching from it (and to fall back per repo when it doesn't).</summary>
    bool RefExists(string repo, string reference);

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
