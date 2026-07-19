using Sprig.Core.Git;

namespace Sprig.Core.Workspaces;

/// <summary>The reconciliation state of a single worktree vs. what the record expects (S3 matrix).</summary>
public enum WorktreeState
{
    /// <summary>Registered with git and present on disk.</summary>
    Healthy,
    /// <summary>Registered but the folder was deleted (Drift A → prune).</summary>
    MissingFolder,
    /// <summary>Folder on disk but git no longer registers it (Drift B → remove folder).</summary>
    Orphaned,
    /// <summary>Neither registered nor on disk (already gone).</summary>
    Gone,
}

/// <summary>Shared worktree state classification + safe filesystem helpers used by create/teardown/reconcile.</summary>
internal static class WorktreeInspector
{
    public static WorktreeState Classify(IGitService git, string repoRoot, string worktreePath)
    {
        var dirExists = Directory.Exists(worktreePath);
        var registered = false;

        if (git.IsGitRepo(repoRoot))
            registered = git.ListWorktrees(repoRoot).Any(w => PathEquals(w.Path, worktreePath));

        return (registered, dirExists) switch
        {
            (true, true) => WorktreeState.Healthy,
            (true, false) => WorktreeState.MissingFolder,
            (false, true) => WorktreeState.Orphaned,
            (false, false) => WorktreeState.Gone,
        };
    }

    public static bool PathEquals(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd('\\', '/');

    /// <summary>Delete a directory, retrying briefly on Windows file locks (S3 note).</summary>
    public static void TryDeleteDirectory(string path)
    {
        for (var i = 0; i < 8; i++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * (i + 1));
            }
        }
    }
}
