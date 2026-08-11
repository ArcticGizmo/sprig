using Sprig.Core.Git;

namespace Sprig.Tests;

/// <summary>A controllable <see cref="IGitService"/> for deterministic classification tests.</summary>
public sealed class FakeGitService : IGitService
{
    public bool RepoExists { get; set; } = true;
    public List<string> TrackedFiles { get; } = [];
    public List<string> IgnoredFiles { get; } = [];
    public List<WorktreeInfo> Worktrees { get; } = [];
    public List<string> Pruned { get; } = [];
    public List<string> RemovedWorktrees { get; } = [];
    public List<string> DeletedBranches { get; } = [];
    public List<string> Fetched { get; } = [];
    public List<(string Repo, string Reference)> HardResets { get; } = [];
    public string DefaultBase { get; set; } = "main";
    public int CommitsAhead { get; set; }

    public bool IsGitRepo(string path) => RepoExists;
    public IReadOnlyCollection<string> ListTrackedFiles(string repo) => TrackedFiles;
    public bool IsIgnored(string repo, string relativePath) => IgnoredFiles.Contains(relativePath);
    public string ResolveRepoRoot(string path) => path;
    public bool BranchExists(string repo, string branch) => true;
    public void AddWorktree(string repo, string worktreePath, string branch)
        => Worktrees.Add(new WorktreeInfo(worktreePath, "head", branch, false));
    public IReadOnlyList<WorktreeInfo> ListWorktrees(string repo) => Worktrees;
    public void RemoveWorktree(string repo, string worktreePath) => RemovedWorktrees.Add(worktreePath);
    public void Prune(string repo) => Pruned.Add(repo);
    public void DeleteBranch(string repo, string branch) => DeletedBranches.Add(branch);
    public void Fetch(string repo) => Fetched.Add(repo);
    public string ResolveDefaultBase(string repo) => DefaultBase;
    public void ResetHard(string repo, string reference) => HardResets.Add((repo, reference));
    public int CountCommitsAhead(string repo, string baseRef) => CommitsAhead;
}
