using Sprig.Core.Git;

namespace Sprig.Tests.Git;

public class WorktreeHeadTests
{
    [Fact]
    public void Parses_a_branch_ref()
    {
        var head = WorktreeHead.Parse("ref: refs/heads/feat/risk-overrides\n");
        Assert.Equal("feat/risk-overrides", head!.Branch);
        Assert.False(head.IsDetached);
    }

    [Fact]
    public void Parses_a_detached_commit()
    {
        var head = WorktreeHead.Parse("3f2a9c1e0b7d4a6f8e5c2b1a0d9e8f7a6b5c4d3e\n");
        Assert.True(head!.IsDetached);
        Assert.Equal("3f2a9c1e0b7d4a6f8e5c2b1a0d9e8f7a6b5c4d3e", head.Commit);
    }

    [Fact]
    public void Rejects_garbage() => Assert.Null(WorktreeHead.Parse("not a head"));

    [Fact]
    public void Missing_path_reads_as_unknown()
        => Assert.Null(WorktreeHead.Read(Path.Combine(Path.GetTempPath(), "sprig-no-such-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Reads_the_main_checkout()
    {
        using var repo = new TempGitRepo();
        Assert.Equal("main", WorktreeHead.Read(repo.Path)!.Branch);
    }

    [Fact]
    public void Follows_a_linked_worktree_as_git_moves_it()
    {
        using var repo = new TempGitRepo();
        var wt = repo.SiblingWorktree("ws1");
        repo.Git("worktree", "add", "--detach", wt);
        Assert.True(WorktreeHead.Read(wt)!.IsDetached);

        // A `git switch` inside the worktree is what made a stored branch go stale; the live read follows it.
        repo.Git("-C", wt, "switch", "-c", "feat/live");
        Assert.Equal("feat/live", WorktreeHead.Read(wt)!.Branch);
    }
}
