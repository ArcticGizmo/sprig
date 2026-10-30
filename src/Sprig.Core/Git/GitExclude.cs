namespace Sprig.Core.Git;

/// <summary>
/// Adds patterns to a repo's <c>info/exclude</c>: the untracked, per-clone ignore file. Lets sprig hide what it
/// puts inside a source repo without ever editing a tracked <c>.gitignore</c>.
/// </summary>
public static class GitExclude
{
    /// <summary>Append <paramref name="pattern"/> to the repo's exclude file unless an identical line is already
    /// there. Best-effort: false when the repo's git directory can't be found or written.</summary>
    public static bool Ensure(string repoRoot, string pattern)
    {
        try
        {
            if (CommonDir(repoRoot) is not { } common) return false;
            var file = Path.Combine(common, "info", "exclude");
            var existing = File.Exists(file) ? File.ReadAllLines(file) : [];
            if (existing.Any(l => l.Trim() == pattern)) return true;

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var text = File.Exists(file) ? File.ReadAllText(file) : "";
            var lead = text.Length == 0 || text.EndsWith('\n') ? "" : Environment.NewLine;
            File.AppendAllText(file, $"{lead}# added by sprig{Environment.NewLine}{pattern}{Environment.NewLine}");
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // info/exclude lives in the common git dir, which a linked worktree reaches through its gitdir's `commondir`.
    static string? CommonDir(string repoRoot)
    {
        var dotGit = Path.Combine(repoRoot, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;

        const string prefix = "gitdir:";
        var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null) return null;
        var gitDir = Path.GetFullPath(line[prefix.Length..].Trim(), repoRoot);
        var commonFile = Path.Combine(gitDir, "commondir");
        return File.Exists(commonFile)
            ? Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDir)
            : gitDir;
    }
}
