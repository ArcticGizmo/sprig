using Sprig.Core.Processes;

namespace Sprig.Tests;

/// <summary>
/// A hermetic git repo in a throwaway directory, with one seed commit. Worktrees are created as
/// siblings under the same <see cref="Root"/>, so disposing deletes the repo and every worktree.
/// Preferred over touching the user's real example repos.
/// </summary>
public sealed class TempGitRepo : IDisposable
{
    static readonly ProcessRunner Runner = new();

    /// <summary>Parent dir containing the repo and any sibling worktrees.</summary>
    public string Root { get; }
    /// <summary>The repo working tree (<c>Root/repo</c>).</summary>
    public string Path { get; }

    public TempGitRepo(string repoName = "repo")
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sprig-git-" + Guid.NewGuid().ToString("N"));
        Path = System.IO.Path.Combine(Root, repoName);
        Directory.CreateDirectory(Path);

        Git("init", "-b", "main");
        // A seed commit so `worktree add` has a HEAD to branch from.
        File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "seed\n");
        Git("add", "-A");
        Git("-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", "seed");
    }

    /// <summary>The sibling worktree path sprig would use for a workspace.</summary>
    public string SiblingWorktree(string workspace)
        => System.IO.Path.Combine(Root, System.IO.Path.GetFileName(Path) + "--" + workspace);

    public ProcessResult Git(params string[] args) => Runner.Run("git", args, Path).EnsureSuccess();

    public void Dispose()
    {
        for (var i = 0; i < 5; i++)
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); return; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
