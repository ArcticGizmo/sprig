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

    public void AddWorktree(string repo, string worktreePath, string branch)
        => runner.Run("git", ["-C", repo, "worktree", "add", worktreePath, "-b", branch], repo).EnsureSuccess();

    // Best-effort: swallow the result so a repo with no remote (fetch exits non-zero) still refreshes
    // against its local base branch instead of the whole operation failing here.
    public void Fetch(string repo)
        => runner.Run("git", ["-C", repo, "fetch", "--all", "--prune"], repo);

    public string ResolveDefaultBase(string repo)
    {
        // Prefer the remote's default branch (origin/HEAD → "origin/main"); the abbrev-ref form gives
        // the branch name directly when origin/HEAD is set.
        var head = runner.Run("git", ["-C", repo, "rev-parse", "--abbrev-ref", "origin/HEAD"], repo);
        if (head.Success)
        {
            var name = head.StdOut.Trim();
            if (name.Length > 0 && name != "origin/HEAD") return name;
        }
        // Fall back through the usual suspects — a remote branch first (real dev), then a local one
        // (a purely-local repo, and the shape most tests use).
        foreach (var candidate in new[] { "origin/main", "origin/master", "main", "master" })
            if (RefExists(repo, candidate)) return candidate;
        throw new InvalidOperationException(
            $"could not determine a base branch for '{repo}' (looked for origin/HEAD, main, master)");
    }

    public void ResetHard(string repo, string reference)
        => runner.Run("git", ["-C", repo, "reset", "--hard", reference], repo).EnsureSuccess();

    public int CountCommitsAhead(string repo, string baseRef)
    {
        var r = runner.Run("git", ["-C", repo, "rev-list", "--count", $"{baseRef}..HEAD"], repo);
        return r.Success && int.TryParse(r.StdOut.Trim(), out var n) ? n : 0;
    }

    bool RefExists(string repo, string reference)
        => runner.Run("git", ["-C", repo, "rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"], repo).Success;

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
