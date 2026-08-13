using Sprig.Core.Processes;

namespace Sprig.Core.Git;

/// <summary>Default <see cref="IGitService"/> that shells out to <c>git</c>.</summary>
public sealed class GitService(IProcessRunner runner) : IGitService
{
    public bool IsGitRepo(string path)
    {
        if (!Directory.Exists(path)) return false;
        var r = runner.Run("git", ["-C", path, "rev-parse", "--is-inside-work-tree"], path);
        return r.Success && r.StdOut.Trim() == "true";
    }

    public IReadOnlyCollection<string> ListTrackedFiles(string repo)
    {
        if (!Directory.Exists(repo)) return [];
        // -z: NUL-separated and never quoted, so paths with spaces/unicode come through verbatim.
        // git reports paths relative to the repo root with forward slashes on every platform.
        var r = runner.Run("git", ["-C", repo, "ls-files", "-z"], repo);
        if (!r.Success) return [];
        return r.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public bool IsIgnored(string repo, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || !Directory.Exists(repo)) return false;
        // --no-index: apply gitignore rules regardless of index/working-tree state, so we get the
        // answer for a file that doesn't exist yet. Exit 0 = ignored, 1 = not ignored, 128 = error.
        var r = runner.Run("git", ["-C", repo, "check-ignore", "-q", "--no-index", "--", relativePath], repo);
        return r.Success;
    }

    public string ResolveRepoRoot(string path)
    {
        var r = runner.Run("git", ["-C", path, "rev-parse", "--show-toplevel"], path).EnsureSuccess();
        return r.StdOut.Trim();
    }

    public bool BranchExists(string repo, string branch)
    {
        var r = runner.Run("git", ["-C", repo, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], repo);
        return r.Success;
    }

    public bool RemoteBranchExists(string repo, string branch)
    {
        // Any remote-tracking ref named <remote>/<branch>. for-each-ref with a wildcard matches across all
        // remotes (origin, upstream, …); non-empty stdout means at least one exists.
        var r = runner.Run("git", ["-C", repo, "for-each-ref", "--format=%(refname)", $"refs/remotes/*/{branch}"], repo);
        return r.Success && r.StdOut.Trim().Length > 0;
    }

    public bool IsValidBranchName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        // check-ref-format --branch validates the name as a branch (exit 0) without touching the repo.
        var r = runner.Run("git", ["check-ref-format", "--branch", name], null);
        return r.Success;
    }

    public void AddWorktree(string repo, string worktreePath, string branch)
        => runner.Run("git", ["-C", repo, "worktree", "add", worktreePath, "-b", branch], repo).EnsureSuccess();

    public void AddWorktreeDetached(string repo, string worktreePath, string reference)
        => runner.Run("git", ["-C", repo, "worktree", "add", "--detach", worktreePath, reference], repo).EnsureSuccess();

    public void SwitchNewBranch(string worktreePath, string branch, string? startPoint = null)
    {
        string[] args = startPoint is null
            ? ["-C", worktreePath, "switch", "-c", branch]
            : ["-C", worktreePath, "switch", "-c", branch, startPoint];
        runner.Run("git", args, worktreePath).EnsureSuccess();
    }

    public void DetachTo(string worktreePath, string reference)
        => runner.Run("git", ["-C", worktreePath, "switch", "--detach", reference], worktreePath).EnsureSuccess();

    public bool HasUncommittedChanges(string worktreePath)
    {
        var r = runner.Run("git", ["-C", worktreePath, "status", "--porcelain"], worktreePath);
        return r.Success && r.StdOut.Trim().Length > 0;
    }

    public int CountUnpushedCommits(string worktreePath)
    {
        // Commits on HEAD that no remote-tracking branch contains. --remotes expands to every refs/remotes/*
        // ref; with no remote at all it expands to nothing, so the whole of HEAD counts as unpushed.
        var r = runner.Run("git", ["-C", worktreePath, "rev-list", "--count", "HEAD", "--not", "--remotes"], worktreePath);
        return r.Success && int.TryParse(r.StdOut.Trim(), out var n) ? n : 0;
    }

    // Best-effort: swallow the result so a repo with no remote (fetch exits non-zero) still refreshes
    // against its local base branch instead of the whole operation failing here.
    public void Fetch(string repo)
        => runner.Run("git", ["-C", repo, "fetch", "--all", "--prune"], repo);

    public string ResolveDefaultBase(string repo)
    {
        // Prefer an 'upstream' remote over 'origin'. In a fork/gitflow setup 'origin' is your own fork (whose
        // main drifts far behind), while 'upstream' is the canonical repo you actually branch from — so
        // branching off origin/main gives a stale base. For each remote, take its default branch
        // (<remote>/HEAD → e.g. upstream/main) when set, else the usual main/master. Fall back to a local
        // branch last (a purely-local repo, and the shape most tests use).
        foreach (var remote in new[] { "upstream", "origin" })
        {
            var head = runner.Run("git", ["-C", repo, "rev-parse", "--abbrev-ref", $"{remote}/HEAD"], repo);
            if (head.Success)
            {
                var name = head.StdOut.Trim();
                if (name.Length > 0 && name != $"{remote}/HEAD") return name;
            }
            foreach (var branch in new[] { $"{remote}/main", $"{remote}/master" })
                if (RefExists(repo, branch)) return branch;
        }
        foreach (var candidate in new[] { "main", "master" })
            if (RefExists(repo, candidate)) return candidate;
        throw new InvalidOperationException(
            $"could not determine a base branch for '{repo}' (looked for upstream/origin HEAD, main, master)");
    }

    public IReadOnlyList<BranchRef> ListStartPointCandidates(string repo)
    {
        // Every remote-tracking branch (all remotes) then every local branch — the "start from" picker's
        // options, each with its tip-commit date for recency ordering. Drops the symbolic '<remote>/HEAD'
        // entries, which aren't real start points. Fields are tab-separated (a tab can't appear in a ref).
        var r = runner.Run("git",
            ["-C", repo, "for-each-ref", "--sort=-committerdate",
             "--format=%(refname:short)\t%(committerdate:iso-strict)", "refs/remotes", "refs/heads"], repo);
        if (!r.Success) return [];
        var list = new List<BranchRef>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            var name = (tab < 0 ? line : line[..tab]).Trim();
            if (name.Length == 0 || name.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
            DateTimeOffset? date = tab >= 0 && DateTimeOffset.TryParse(line[(tab + 1)..].Trim(), out var d) ? d : null;
            list.Add(new BranchRef(name, date));
        }
        return list;
    }

    public string? CurrentBranch(string repo)
    {
        // symbolic-ref fails (non-zero) on a detached HEAD, which is exactly when there's no "current branch".
        var r = runner.Run("git", ["-C", repo, "symbolic-ref", "--quiet", "--short", "HEAD"], repo);
        return r.Success && r.StdOut.Trim() is { Length: > 0 } name ? name : null;
    }

    public IReadOnlyList<GraphCommit> ListCommitGraph(string repo, int limit)
    {
        // One line per commit; fields separated by US (0x1f), which can't appear in any of them. --all spans
        // every ref; --date-order gives a stable newest-first order suited to a swimlane layout.
        const string fmt = "%H%x1f%P%x1f%D%x1f%an%x1f%cI%x1f%s";
        var r = runner.Run("git",
            ["-C", repo, "log", "--all", "--date-order", $"--max-count={limit}", "--decorate=short",
             $"--pretty=format:{fmt}"], repo);
        if (!r.Success) return [];
        var commits = new List<GraphCommit>();
        foreach (var raw in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = raw.TrimEnd('\r').Split((char)0x1f); // trim the CRLF's \r so the last field (subject) is clean
            if (f.Length < 6) continue;
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            DateTimeOffset? when = DateTimeOffset.TryParse(f[4], out var d) ? d : null;
            commits.Add(new GraphCommit(f[0], parents, ParseDecorations(f[2]), f[3], when, f[5]));
        }
        return commits;
    }

    // "%D" decorations look like "HEAD -> main, origin/main, tag: v1.0". Strip the HEAD arrow and drop the
    // bare "HEAD" (detached) marker; keep branch names. Tags are dropped — the graph is a branch picker.
    static IReadOnlyList<string> ParseDecorations(string decorations)
    {
        if (string.IsNullOrWhiteSpace(decorations)) return [];
        var refs = new List<string>();
        foreach (var raw in decorations.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw;
            if (name.StartsWith("HEAD -> ", StringComparison.Ordinal)) name = name["HEAD -> ".Length..];
            else if (name == "HEAD") continue;
            if (name.StartsWith("tag: ", StringComparison.Ordinal)) continue;
            if (name.EndsWith("/HEAD", StringComparison.Ordinal)) continue; // e.g. origin/HEAD — not a real branch
            refs.Add(name);
        }
        return refs;
    }

    public bool RefExists(string repo, string reference)
        => runner.Run("git", ["-C", repo, "rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"], repo).Success;

    public void ResetHard(string repo, string reference)
        => runner.Run("git", ["-C", repo, "reset", "--hard", reference], repo).EnsureSuccess();

    public int CountCommitsAhead(string repo, string baseRef)
    {
        var r = runner.Run("git", ["-C", repo, "rev-list", "--count", $"{baseRef}..HEAD"], repo);
        return r.Success && int.TryParse(r.StdOut.Trim(), out var n) ? n : 0;
    }

    public IReadOnlyList<WorktreeInfo> ListWorktrees(string repo)
    {
        var r = runner.Run("git", ["-C", repo, "worktree", "list", "--porcelain"], repo).EnsureSuccess();
        return ParsePorcelain(r.StdOut);
    }

    public void RemoveWorktree(string repo, string worktreePath)
        => runner.Run("git", ["-C", repo, "worktree", "remove", "--force", worktreePath], repo).EnsureSuccess();

    public void Prune(string repo)
        => runner.Run("git", ["-C", repo, "worktree", "prune"], repo).EnsureSuccess();

    public void DeleteBranch(string repo, string branch)
        => runner.Run("git", ["-C", repo, "branch", "-D", branch], repo).EnsureSuccess();

    // Porcelain format: attribute lines per worktree, blocks separated by a blank line.
    //   worktree <path>
    //   HEAD <sha>
    //   branch refs/heads/<name>   |   detached   |   bare
    //   prunable <reason>          (optional)
    internal static IReadOnlyList<WorktreeInfo> ParsePorcelain(string output)
    {
        var results = new List<WorktreeInfo>();
        string? path = null, head = null, branch = null;
        bool prunable = false, bare = false, detached = false;

        void Flush()
        {
            if (path is not null)
                results.Add(new WorktreeInfo(path, head, branch, prunable, bare, detached));
            path = head = branch = null;
            prunable = bare = detached = false;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? "" : line[(space + 1)..];

            switch (key)
            {
                case "worktree": Flush(); path = value; break;
                case "HEAD": head = value; break;
                case "branch": branch = StripRefPrefix(value); break;
                case "detached": detached = true; break;
                case "bare": bare = true; break;
                case "prunable": prunable = true; break;
            }
        }
        Flush();
        return results;
    }

    static string StripRefPrefix(string reference)
        => reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : reference;
}
