using Sprig.Engine.Processes;

namespace Sprig.Engine.Git;

/// <summary>The git operations a sprout needs, by shelling out to <c>git</c>.</summary>
public sealed class GitClient(IProcessRunner runner)
{
    public bool IsRepo(string path)
        => Directory.Exists(path) && Run(path, "rev-parse", "--is-inside-work-tree") is { Success: true } r && r.StdOut.Trim() == "true";

    /// <summary>The top of the working tree that contains <paramref name="path"/>.</summary>
    public string TopLevel(string path) => Normalise(Run(path, "rev-parse", "--show-toplevel").EnsureSuccess().StdOut.Trim());

    /// <summary>True when git tracks <paramref name="file"/> (absolute, inside <paramref name="repo"/>).</summary>
    public bool IsTracked(string repo, string file)
        => Run(repo, "ls-files", "--error-unmatch", "--", file).Success;

    /// <summary>True when an ignore rule (.gitignore, info/exclude or the global excludes file) covers
    /// <paramref name="file"/>, whether or not it exists yet.</summary>
    public bool IsIgnored(string repo, string file)
        => Run(repo, "check-ignore", "-q", "--no-index", "--", file).Success;

    /// <summary>
    /// Whether git tracks each of <paramref name="files"/> (absolute, inside <paramref name="repo"/>) and whether an
    /// ignore rule covers it, existing or not, in two calls however many files there are; each git call costs a few
    /// hundred milliseconds on Windows. Null when <paramref name="repo"/> isn't in a git working tree.
    /// </summary>
    public IReadOnlyDictionary<string, (bool Tracked, bool Ignored)>? Status(string repo, IReadOnlyList<string> files)
    {
        if (!Directory.Exists(repo)) return null;
        var relative = files.Select(f => Path.GetRelativePath(repo, f).Replace('\\', '/')).ToArray();
        var tracked = Run(repo, ["ls-files", "-z", "--", .. relative]);
        if (!tracked.Success) return null;
        // check-ignore exits 1 when it matches nothing, which is an answer, not a failure.
        // Its -z needs --stdin, so read lines, with quoting off so a non-ASCII path comes back as it went in.
        var ignored = Run(repo, ["-c", "core.quotePath=false", "check-ignore", "--no-index", "--", .. relative]);
        if (ignored.ExitCode > 1) ignored.EnsureSuccess();

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var trackedSet = tracked.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(comparer);
        var ignoredSet = ignored.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(comparer);
        var result = new Dictionary<string, (bool, bool)>(comparer);
        for (var i = 0; i < files.Count; i++) result[files[i]] = (trackedSet.Contains(relative[i]), ignoredSet.Contains(relative[i]));
        return result;
    }

    public bool BranchExists(string repo, string branch)
        => Run(repo, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").Success;

    public bool RefExists(string repo, string reference)
        => Run(repo, "rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}").Success;

    /// <summary>A remote-tracking branch named <paramref name="branch"/>, preferring origin then upstream.</summary>
    public string? RemoteBranch(string repo, string branch)
    {
        var refs = Run(repo, "for-each-ref", "--format=%(refname:short)", $"refs/remotes/*/{branch}").StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return refs.FirstOrDefault(r => r.StartsWith("origin/", StringComparison.Ordinal))
            ?? refs.FirstOrDefault(r => r.StartsWith("upstream/", StringComparison.Ordinal))
            ?? refs.FirstOrDefault();
    }

    public bool IsValidBranchName(string name)
        => !string.IsNullOrWhiteSpace(name) && runner.Run("git", ["check-ref-format", "--branch", name]).Success;

    /// <summary>
    /// A worktree at <paramref name="path"/> on <paramref name="branch"/>: the local branch when there is one, else a
    /// remote one of that name, else a new branch from <paramref name="from"/>. Returns true when it made the branch.
    /// </summary>
    public bool AddWorktree(string repo, string path, string branch, string from)
    {
        if (BranchExists(repo, branch))
        {
            Run(repo, "worktree", "add", path, branch).EnsureSuccess();
            return false;
        }
        if (RemoteBranch(repo, branch) is { } remote)
        {
            Run(repo, "worktree", "add", "--track", "-b", branch, path, remote).EnsureSuccess();
            return true;
        }
        var start = RefExists(repo, from) ? from
            : RemoteBranch(repo, from) ?? throw new InvalidOperationException($"{repo} has no branch '{from}' to start '{branch}' from");
        Run(repo, "worktree", "add", "-b", branch, path, start).EnsureSuccess();
        return true;
    }

    public IReadOnlyList<string> Worktrees(string repo)
        => Run(repo, "worktree", "list", "--porcelain").EnsureSuccess().StdOut
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => Normalise(l["worktree ".Length..]))
            .ToArray();

    public bool HasUncommittedChanges(string path)
        => Run(path, "status", "--porcelain").StdOut.Trim().Length > 0;

    /// <summary>Commits that only <paramref name="branch"/> has: no other branch and no remote, so deleting the branch
    /// would lose them.</summary>
    public int CommitsOnlyOn(string repo, string branch)
        => Run(repo, "rev-list", "--count", branch, "--not", $"--exclude={branch}", "--branches", "--remotes") is { Success: true } r
           && int.TryParse(r.StdOut.Trim(), out var n) ? n : 0;

    public void RemoveWorktree(string repo, string path, bool force)
    {
        string[] args = force ? ["worktree", "remove", "--force", path] : ["worktree", "remove", path];
        Run(repo, args).EnsureSuccess();
    }

    public void DeleteBranch(string repo, string branch, bool force)
        => Run(repo, "branch", force ? "-D" : "-d", branch).EnsureSuccess();

    ProcessResult Run(string repo, params string[] args)
        => runner.Run("git", ["-C", repo, .. args], Directory.Exists(repo) ? repo : null);

    /// <summary>git prints forward slashes; compare and store paths the way the OS spells them.</summary>
    static string Normalise(string path) => Path.GetFullPath(path);
}
