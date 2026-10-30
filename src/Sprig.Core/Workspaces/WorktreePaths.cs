using Sprig.Core.Settings;

namespace Sprig.Core.Workspaces;

/// <summary>Where a new workspace's worktree goes for a given <see cref="WorktreeLayout"/>.</summary>
public static class WorktreePaths
{
    /// <summary>Claude Code's worktree folder, relative to the repo root (forward slashes, as git and gitignore want).</summary>
    public const string ClaudeWorktreesDir = ".claude/worktrees";

    /// <summary>
    /// The worktree path and, for <see cref="WorktreeLayout.Claude"/>, the sibling link path. The nested path is
    /// the real one because Claude Code resolves links before deciding whether a worktree is its own; the
    /// sibling can therefore only ever be the link.
    /// </summary>
    public static (string Worktree, string? Link) For(string repoRoot, string workspace, WorktreeLayout layout)
    {
        // git reports roots with forward slashes; normalise so stored paths and link targets use one form.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        var parent = Directory.GetParent(root)?.FullName
            ?? throw new WorkspaceException($"repo '{repoRoot}' has no parent directory for a sibling worktree");
        var sibling = Path.Combine(parent, $"{Path.GetFileName(root)}--{workspace}");
        return layout switch
        {
            WorktreeLayout.Claude => (Path.Combine(root, ".claude", "worktrees", workspace), sibling),
            _ => (sibling, null),
        };
    }
}
