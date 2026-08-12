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

    // New-model (detached-slot / branch-on-claim) knobs and capture lists.
    public HashSet<string> LocalBranches { get; } = [];
    public HashSet<string> RemoteBranches { get; } = [];
    public List<(string Worktree, string Branch, string? StartPoint)> SwitchedNewBranches { get; } = [];
    public List<(string Worktree, string Reference)> Detached { get; } = [];
    public int UnpushedCommits { get; set; }
    public bool Dirty { get; set; }

    public bool IsGitRepo(string path) => RepoExists;
    public IReadOnlyCollection<string> ListTrackedFiles(string repo) => TrackedFiles;
    public bool IsIgnored(string repo, string relativePath) => IgnoredFiles.Contains(relativePath);
    public string ResolveRepoRoot(string path) => path;
    public bool BranchExists(string repo, string branch) => LocalBranches.Contains(branch);
    public bool RemoteBranchExists(string repo, string branch) => RemoteBranches.Contains(branch);
    public bool IsValidBranchName(string name) =>
        !string.IsNullOrWhiteSpace(name) && !name.StartsWith('-') && !name.Contains("..") && !name.EndsWith(".lock");
    public void AddWorktree(string repo, string worktreePath, string branch)
        => Worktrees.Add(new WorktreeInfo(worktreePath, "head", branch, false));
    public void AddWorktreeDetached(string repo, string worktreePath, string reference)
        => Worktrees.Add(new WorktreeInfo(worktreePath, "head", null, false, false, true));
    public void SwitchNewBranch(string worktreePath, string branch, string? startPoint = null)
        => SwitchedNewBranches.Add((worktreePath, branch, startPoint));
    public void DetachTo(string worktreePath, string reference) => Detached.Add((worktreePath, reference));
    public bool HasUncommittedChanges(string worktreePath) => Dirty;
    public int CountUnpushedCommits(string worktreePath) => UnpushedCommits;
    public IReadOnlyList<WorktreeInfo> ListWorktrees(string repo) => Worktrees;
    public void RemoveWorktree(string repo, string worktreePath) => RemovedWorktrees.Add(worktreePath);
    public void Prune(string repo) => Pruned.Add(repo);
    public void DeleteBranch(string repo, string branch) => DeletedBranches.Add(branch);
    public void Fetch(string repo) => Fetched.Add(repo);
    public string ResolveDefaultBase(string repo) => DefaultBase;
    public void ResetHard(string repo, string reference) => HardResets.Add((repo, reference));
    public int CountCommitsAhead(string repo, string baseRef) => CommitsAhead;
}
