namespace Sprig.Core.Git;

/// <summary>What a worktree's HEAD points at right now: a branch (short name), or a detached commit.</summary>
public sealed record WorktreeHead(string? Branch, string? Commit)
{
    public bool IsDetached => Branch is null;

    /// <summary>
    /// Read a worktree's HEAD straight from disk (<c>.git</c> → gitdir → <c>HEAD</c>) instead of running git.
    /// Listing many workspaces would otherwise spawn a git process per repo per refresh, which is slow and
    /// trips endpoint-protection heuristics on locked-down machines. Returns null when the path isn't a
    /// readable git checkout (missing worktree, unreadable file) — callers show that as unknown.
    /// </summary>
    public static WorktreeHead? Read(string worktreePath)
    {
        try
        {
            if (GitDir(worktreePath) is not { } gitDir) return null;
            var headFile = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headFile)) return null;
            return Parse(File.ReadAllText(headFile));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Parse the contents of a HEAD file: <c>ref: refs/heads/&lt;branch&gt;</c> or a bare commit id.</summary>
    public static WorktreeHead? Parse(string content)
    {
        var line = content.Trim();
        const string refPrefix = "ref:";
        if (line.StartsWith(refPrefix, StringComparison.Ordinal))
        {
            var target = line[refPrefix.Length..].Trim();
            const string heads = "refs/heads/";
            return new WorktreeHead(target.StartsWith(heads, StringComparison.Ordinal) ? target[heads.Length..] : target, null);
        }
        return line.Length > 0 && line.All(Uri.IsHexDigit) ? new WorktreeHead(null, line) : null;
    }

    // A linked worktree's `.git` is a file naming its gitdir (absolute, or relative to the worktree); a main
    // checkout's `.git` is the directory itself.
    static string? GitDir(string worktreePath)
    {
        var dotGit = Path.Combine(worktreePath, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;

        const string prefix = "gitdir:";
        var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null) return null;
        var dir = line[prefix.Length..].Trim();
        return Path.IsPathRooted(dir) ? dir : Path.GetFullPath(Path.Combine(worktreePath, dir));
    }
}
