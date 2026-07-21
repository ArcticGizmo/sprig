using Sprig.Core.Git;
using Sprig.Core.Processes;

namespace Sprig.Tests.Git;

public class GitServiceTests
{
    static GitService NewService() => new(new ProcessRunner());

    [Fact]
    public void IsGitRepo_true_for_repo_false_for_plain_dir()
    {
        using var repo = new TempGitRepo();
        var git = NewService();

        Assert.True(git.IsGitRepo(repo.Path));

        var plain = Directory.CreateTempSubdirectory("sprig-plain-");
        try { Assert.False(git.IsGitRepo(plain.FullName)); }
        finally { plain.Delete(recursive: true); }
    }

    [Fact]
    public void ListTrackedFiles_returns_committed_paths_and_empty_for_non_repo()
    {
        using var repo = new TempGitRepo();
        var git = NewService();

        Assert.Contains("README.md", git.ListTrackedFiles(repo.Path)); // the seed commit

        var plain = Directory.CreateTempSubdirectory("sprig-plain-");
        try { Assert.Empty(git.ListTrackedFiles(plain.FullName)); } // not a repo → empty, no throw
        finally { plain.Delete(recursive: true); }
    }

    [Fact]
    public void IsIgnored_applies_gitignore_rules_even_for_paths_that_do_not_exist()
    {
        using var repo = new TempGitRepo();
        var git = NewService();
        File.WriteAllText(Path.Combine(repo.Path, ".gitignore"), ".env.local\n");

        // matched by a rule though the file was never created — the --no-index answer we rely on
        Assert.True(git.IsIgnored(repo.Path, ".env.local"));
        // not matched by any rule
        Assert.False(git.IsIgnored(repo.Path, ".env.shared"));
        // a committed, non-ignored file is not "ignored"
        Assert.False(git.IsIgnored(repo.Path, "README.md"));
    }

    [Fact]
    public void Add_list_remove_worktree_round_trip()
    {
        using var repo = new TempGitRepo();
        var git = NewService();
        var wt = repo.SiblingWorktree("feat-a");

        git.AddWorktree(repo.Path, wt, "sprig/feat-a");
        Assert.True(Directory.Exists(wt));
        Assert.True(git.BranchExists(repo.Path, "sprig/feat-a"));

        var list = git.ListWorktrees(repo.Path);
        Assert.Contains(list, w => SamePath(w.Path, wt) && w.Branch == "sprig/feat-a" && !w.IsPrunable);

        git.RemoveWorktree(repo.Path, wt);
        Assert.False(Directory.Exists(wt));
        // Branch survives worktree removal (S3 finding).
        Assert.True(git.BranchExists(repo.Path, "sprig/feat-a"));
    }

    [Fact]
    public void Remove_forces_even_with_untracked_files()
    {
        using var repo = new TempGitRepo();
        var git = NewService();
        var wt = repo.SiblingWorktree("dirty");
        git.AddWorktree(repo.Path, wt, "sprig/dirty");

        // A clobbered .env is untracked — plain remove would refuse; RemoveWorktree uses --force.
        File.WriteAllText(Path.Combine(wt, ".env"), "PORT=20001\n");
        git.RemoveWorktree(repo.Path, wt);

        Assert.False(Directory.Exists(wt));
    }

    [Fact]
    public void Deleted_folder_shows_prunable_then_prune_clears()
    {
        using var repo = new TempGitRepo();
        var git = NewService();
        var wt = repo.SiblingWorktree("gone");
        git.AddWorktree(repo.Path, wt, "sprig/gone");

        Directory.Delete(wt, recursive: true); // simulate manual deletion (Drift A)

        Assert.Contains(git.ListWorktrees(repo.Path), w => SamePath(w.Path, wt) && w.IsPrunable);

        git.Prune(repo.Path);
        Assert.DoesNotContain(git.ListWorktrees(repo.Path), w => SamePath(w.Path, wt));
    }

    [Fact]
    public void DeleteBranch_removes_it()
    {
        using var repo = new TempGitRepo();
        var git = NewService();
        var wt = repo.SiblingWorktree("b");
        git.AddWorktree(repo.Path, wt, "sprig/b");
        git.RemoveWorktree(repo.Path, wt);

        git.DeleteBranch(repo.Path, "sprig/b");
        Assert.False(git.BranchExists(repo.Path, "sprig/b"));
    }

    static bool SamePath(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
}

public class GitPorcelainParsingTests
{
    [Fact]
    public void Parses_main_and_worktree_with_prunable()
    {
        const string output =
            "worktree C:/repos/app\n" +
            "HEAD abc123\n" +
            "branch refs/heads/main\n" +
            "\n" +
            "worktree C:/repos/app--feat\n" +
            "HEAD def456\n" +
            "branch refs/heads/sprig/feat\n" +
            "prunable gitdir file points to non-existent location\n" +
            "\n";

        var list = GitService.ParsePorcelain(output);

        Assert.Equal(2, list.Count);
        Assert.Equal("main", list[0].Branch);
        Assert.False(list[0].IsPrunable);
        Assert.Equal("sprig/feat", list[1].Branch);
        Assert.True(list[1].IsPrunable);
        Assert.Equal("def456", list[1].Head);
    }

    [Fact]
    public void Parses_detached_and_bare()
    {
        const string output =
            "worktree /srv/bare\nbare\n\n" +
            "worktree /srv/det\nHEAD f00\ndetached\n\n";

        var list = GitService.ParsePorcelain(output);

        Assert.True(list[0].IsBare);
        Assert.True(list[1].IsDetached);
        Assert.Null(list[1].Branch);
    }
}
