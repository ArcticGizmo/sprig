using Sprig.Engine.Processes;

namespace Sprig.Engine.Git;

/// <summary>A branch: a local one when <paramref name="Remote"/> is null, else a remote-tracking one.</summary>
public sealed record GitBranch(string Name, string? Remote)
{
    /// <summary>As git names it: <c>feat/x</c>, or <c>origin/feat/x</c>.</summary>
    public string Ref => Remote is null ? Name : $"{Remote}/{Name}";
}

/// <summary>What has become of a branch, as git alone can tell.</summary>
public enum BranchFate
{
    /// <summary>Its work isn't in the default branch, and the remote it tracks (if any) still has it.</summary>
    Live,
    /// <summary>No commits of its own and never pushed: just made, so there's nothing to say yet.</summary>
    Empty,
    /// <summary>Everything on it is in the default branch, however it got there: merged, squashed or rebased.</summary>
    Merged,
    /// <summary>The remote it tracked deleted it, but its work isn't in the default branch.</summary>
    Gone,
}

/// <param name="Into">The branch it was checked against, e.g. <c>origin/main</c>.</param>
public sealed record BranchCheck(string Branch, BranchFate Fate, string? Into);

/// <summary>The git operations a workspace needs, by shelling out to <c>git</c>.</summary>
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

    /// <summary>The repo's local branches, then its remote-tracking ones (without each remote's <c>HEAD</c>).</summary>
    public IReadOnlyList<GitBranch> Branches(string repo)
    {
        var result = Run(repo, "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes");
        if (!result.Success) return [];
        var remotes = Run(repo, "remote").StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderByDescending(r => r.Length).ToArray();
        var branches = new List<GitBranch>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("refs/heads/", StringComparison.Ordinal))
                branches.Add(new GitBranch(line["refs/heads/".Length..], null));
            else if (line.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var rest = line["refs/remotes/".Length..];
                // A remote's name can hold a slash, so the longest that fits is the one.
                if (remotes.FirstOrDefault(r => rest.StartsWith(r + "/", StringComparison.Ordinal)) is not { } remote) continue;
                var name = rest[(remote.Length + 1)..];
                if (name != "HEAD") branches.Add(new GitBranch(name, remote));
            }
        }
        return branches;
    }

    /// <summary>A remote-tracking branch named <paramref name="branch"/>: on <paramref name="remote"/> when one is named,
    /// else preferring origin then upstream.</summary>
    public string? RemoteBranch(string repo, string branch, string? remote = null)
    {
        if (remote is not null) return RefExists(repo, $"refs/remotes/{remote}/{branch}") ? $"{remote}/{branch}" : null;
        var refs = Run(repo, "for-each-ref", "--format=%(refname:short)", $"refs/remotes/*/{branch}").StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return refs.FirstOrDefault(r => r.StartsWith("origin/", StringComparison.Ordinal))
            ?? refs.FirstOrDefault(r => r.StartsWith("upstream/", StringComparison.Ordinal))
            ?? refs.FirstOrDefault();
    }

    /// <summary>The branch a repo's new work starts from: what its origin calls default, else a local <c>main</c> or
    /// <c>master</c>, else whatever its checkout is on. Null when none of those can be found.</summary>
    public string? DefaultBranch(string repo)
    {
        var originHead = Run(repo, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD");
        if (originHead.Success && originHead.StdOut.Trim() is { Length: > 0 } head)
            return head.StartsWith("origin/", StringComparison.Ordinal) ? head["origin/".Length..] : head;
        foreach (var name in (string[])["main", "master"])
            if (BranchExists(repo, name)) return name;
        var current = Run(repo, "symbolic-ref", "--quiet", "--short", "HEAD");
        return current.Success && current.StdOut.Trim() is { Length: > 0 } branch ? branch : null;
    }

    /// <summary>
    /// Bring every remote's branches up to date and drop the ones the remotes deleted, so a branch whose remote went reads
    /// as gone. False when it couldn't, such as offline or past <paramref name="timeout"/>; what was fetched before still
    /// answers.
    /// </summary>
    public bool FetchPrune(string repo, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            return runner.Run("git", ["-C", repo, "fetch", "--all", "--prune", "--quiet"], repo, limit.Token).Success;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="branch"/> has been merged or its remote deleted, read from refs already fetched (see
    /// <see cref="FetchPrune"/>), so nothing here talks to a remote. Merged means merging it into the default branch would
    /// change nothing, which holds for squash and rebase merges too, not only merge commits.
    /// </summary>
    public BranchCheck CheckBranch(string repo, string branch)
    {
        var tracking = Run(repo, "for-each-ref", "--format=%(upstream)|%(upstream:track)", $"refs/heads/{branch}").StdOut.Trim();
        var pushed = !tracking.StartsWith('|') && tracking.Length > 0;
        var gone = tracking.EndsWith("[gone]", StringComparison.Ordinal);

        var into = DefaultBranch(repo) is { } name ? RemoteBranch(repo, name, "origin") ?? RemoteBranch(repo, name) ?? name : null;
        if (into is null || !RefExists(repo, branch)) return new BranchCheck(branch, gone ? BranchFate.Gone : BranchFate.Live, into);

        // Its tip is already in the default branch: either merged as it stood, or made and never committed to.
        if (Run(repo, "merge-base", "--is-ancestor", branch, into).Success)
            return new BranchCheck(branch, pushed ? BranchFate.Merged : BranchFate.Empty, into);

        // A squash or rebase leaves the branch's commits out of the default branch but its changes in; merging it again
        // then gives back the default branch's own tree. A conflict exits 1, which means there's work still to land.
        var merged = Run(repo, "merge-tree", "--write-tree", "--no-messages", into, branch);
        var target = Run(repo, "rev-parse", $"{into}^{{tree}}");
        var landed = merged.Success && target.Success
            && merged.StdOut.Split('\n')[0].Trim() == target.StdOut.Trim();
        return new BranchCheck(branch, landed ? BranchFate.Merged : gone ? BranchFate.Gone : BranchFate.Live, into);
    }

    public bool IsValidBranchName(string name)
        => !string.IsNullOrWhiteSpace(name) && runner.Run("git", ["check-ref-format", "--branch", name]).Success;

    /// <summary>
    /// A worktree at <paramref name="path"/> on <paramref name="branch"/>: the local branch when there is one, else a
    /// remote one of that name, else a new branch from <paramref name="from"/>. Returns true when it made the branch.
    /// </summary>
    /// <param name="track">The remote to take the branch from when there's no local one; null for whichever has it.</param>
    public bool AddWorktree(string repo, string path, string branch, string from, string? track = null)
    {
        if (BranchExists(repo, branch))
        {
            Run(repo, "worktree", "add", path, branch).EnsureSuccess();
            return false;
        }
        if (RemoteBranch(repo, branch, track) is { } remote)
        {
            Run(repo, "worktree", "add", "--track", "-b", branch, path, remote).EnsureSuccess();
            return true;
        }
        var start = RefExists(repo, from) ? from
            : RemoteBranch(repo, from) ?? throw new InvalidOperationException($"{repo} has no branch '{from}' to start '{branch}' from");
        Run(repo, "worktree", "add", "-b", branch, path, start).EnsureSuccess();
        return true;
    }

    /// <summary>
    /// Switch the worktree at <paramref name="worktree"/> to <paramref name="branch"/>, choosing it as
    /// <see cref="AddWorktree"/> does. Returns true when it made the branch.
    /// </summary>
    public bool SwitchTo(string worktree, string branch, string from, string? track = null)
    {
        if (BranchExists(worktree, branch))
        {
            Run(worktree, "switch", branch).EnsureSuccess();
            return false;
        }
        if (RemoteBranch(worktree, branch, track) is { } remote)
        {
            Run(worktree, "switch", "--track", "-c", branch, remote).EnsureSuccess();
            return true;
        }
        var start = RefExists(worktree, from) ? from
            : RemoteBranch(worktree, from) ?? throw new InvalidOperationException($"{worktree} has no branch '{from}' to start '{branch}' from");
        Run(worktree, "switch", "-c", branch, start).EnsureSuccess();
        return true;
    }

    /// <summary>Leave the worktree at its current commit with no branch checked out, so the branch is free for any
    /// other worktree.</summary>
    public void Detach(string worktree) => Run(worktree, "switch", "--detach").EnsureSuccess();

    /// <summary>A worktree at <paramref name="path"/> on a detached HEAD at <paramref name="at"/>, a local or remote
    /// branch or a commit.</summary>
    public void AddDetachedWorktree(string repo, string path, string at)
    {
        var start = RefExists(repo, at) ? at
            : RemoteBranch(repo, at) ?? throw new InvalidOperationException($"{repo} has no branch '{at}' to start a free slot at");
        Run(repo, "worktree", "add", "--detach", path, start).EnsureSuccess();
    }

    /// <summary>The branch checked out at <paramref name="worktree"/>; null on a detached HEAD.</summary>
    public string? CurrentBranch(string worktree)
        => Run(worktree, "symbolic-ref", "--quiet", "--short", "HEAD") is { Success: true } r && r.StdOut.Trim() is { Length: > 0 } b ? b : null;

    /// <summary>The worktree of <paramref name="repo"/>, its own checkout included, that has <paramref name="branch"/>
    /// checked out; null when none has.</summary>
    public string? WorktreeOn(string repo, string branch)
    {
        string? path = null;
        foreach (var line in Run(repo, "worktree", "list", "--porcelain").EnsureSuccess().StdOut.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = Normalise(line["worktree ".Length..]);
            else if (line == $"branch refs/heads/{branch}") return path;
        }
        return null;
    }

    public IReadOnlyList<string> Worktrees(string repo)
        => Run(repo, "worktree", "list", "--porcelain").EnsureSuccess().StdOut
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => Normalise(l["worktree ".Length..]))
            .ToArray();

    /// <summary>The text <paramref name="file"/> has in its working tree's last commit; null when it isn't in a git
    /// working tree or that commit doesn't have it.</summary>
    public string? CommittedText(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        if (!Directory.Exists(dir)) return null;
        // "./" makes the path relative to -C's folder rather than the top of the working tree.
        var result = Run(dir, "show", $"HEAD:./{Path.GetFileName(file)}");
        return result.Success ? result.StdOut : null;
    }

    public bool HasUncommittedChanges(string path)
        => Run(path, "status", "--porcelain").StdOut.Trim().Length > 0;

    /// <summary>Commits that only <paramref name="branch"/> has: no other branch and no remote, so deleting the branch
    /// would lose them.</summary>
    public int CommitsOnlyOn(string repo, string branch)
        => Run(repo, "rev-list", "--count", branch, "--not", $"--exclude={branch}", "--branches", "--remotes") is { Success: true } r
           && int.TryParse(r.StdOut.Trim(), out var n) ? n : 0;

    /// <summary>Forget worktrees whose folders are gone.</summary>
    public void PruneWorktrees(string repo) => Run(repo, "worktree", "prune").EnsureSuccess();

    /// <summary>The checkout <paramref name="worktree"/> is a worktree of (itself, for a repo's own checkout); null when
    /// it isn't in a git repo.</summary>
    public string? MainCheckout(string worktree)
        => Run(worktree, "rev-parse", "--path-format=absolute", "--git-common-dir") is { Success: true } r && r.StdOut.Trim() is { Length: > 0 } common
            ? Normalise(Path.GetDirectoryName(Path.GetFullPath(common.TrimEnd('/', '\\')))!)
            : null;

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
