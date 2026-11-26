using Sprig.Engine.Git;
using Sprig.Engine.Processes;
using Sprig.Engine.Store;

namespace Sprig.Tests.Git;

/// <summary>Branches merged and deleted the ways a code host does it, by a second clone of a bare origin, then checked from
/// the first clone with nothing but git.</summary>
public sealed class BranchCheckTests : IDisposable
{
    readonly string _root = LongPath.Of(Path.Combine(Path.GetTempPath(), "sprig-tidy-" + Guid.NewGuid().ToString("N")));
    readonly ProcessRunner _processes = new();
    readonly GitClient _git;
    readonly string _mine;
    readonly string _host;

    public BranchCheckTests()
    {
        _git = new GitClient(_processes);
        Directory.CreateDirectory(_root);
        var origin = Path.Combine(_root, "origin.git");
        Git(_root, "init", "-q", "--bare", "-b", "main", origin);
        _mine = Path.Combine(_root, "mine");
        Git(_root, "clone", "-q", origin, _mine);
        Commit(_mine, "readme.md", "hello\n", "init");
        Git(_mine, "push", "-q", "-u", "origin", "main");
        Git(_mine, "remote", "set-head", "origin", "main");
        _host = Path.Combine(_root, "host");
        Git(_root, "clone", "-q", origin, _host);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    void Git(string dir, params string[] args)
        => _processes.Run("git", ["-C", dir, "-c", "user.name=t", "-c", "user.email=t@example.com", .. args]).EnsureSuccess();

    void Commit(string dir, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        Git(dir, "add", "-A");
        Git(dir, "commit", "-q", "-m", message);
    }

    /// <summary>A branch of two commits in my clone, pushed.</summary>
    void Feature(string branch)
    {
        Git(_mine, "switch", "-q", "-c", branch, "main");
        Commit(_mine, $"{branch}.txt", "one\n", "one");
        Commit(_mine, $"{branch}.txt", "one\ntwo\n", "two");
        Git(_mine, "push", "-q", "-u", "origin", branch);
        Git(_mine, "switch", "-q", "main");
    }

    /// <summary>The host merges the branch its way, then deletes it from origin.</summary>
    void HostMerges(string branch, string how, bool deleteBranch = true)
    {
        Git(_host, "fetch", "-q", "origin");
        Git(_host, "switch", "-q", "main");
        Git(_host, "reset", "-q", "--hard", "origin/main");
        switch (how)
        {
            case "merge": Git(_host, "merge", "-q", "--no-ff", "-m", "merge", $"origin/{branch}"); break;
            case "squash":
                Git(_host, "merge", "-q", "--squash", $"origin/{branch}");
                Git(_host, "commit", "-q", "-m", $"{branch} (#1)");
                break;
            case "rebase": Git(_host, "cherry-pick", $"main..origin/{branch}"); break;
        }
        Git(_host, "push", "-q", "origin", "main");
        if (deleteBranch) Git(_host, "push", "-q", "origin", "--delete", branch);
    }

    /// <summary>Main moves on in a file the branch never touched, so the branch's tree is no longer main's.</summary>
    void MainMovesOn(string file = "other.txt")
    {
        Git(_host, "fetch", "-q", "origin");
        Git(_host, "switch", "-q", "main");
        Git(_host, "reset", "-q", "--hard", "origin/main");
        Commit(_host, file, "later\n", "later");
        Git(_host, "push", "-q", "origin", "main");
    }

    BranchFate Check(string branch)
    {
        Assert.True(_git.FetchPrune(_mine, TimeSpan.FromSeconds(30)));
        return _git.CheckBranch(_mine, branch).Fate;
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("squash")]
    [InlineData("rebase")]
    public void A_branch_merged_any_way_and_deleted_is_merged(string how)
    {
        MainMovesOn("before.txt");
        Feature("feat-a");
        HostMerges("feat-a", how);
        MainMovesOn();

        Assert.Equal(BranchFate.Merged, Check("feat-a"));
    }

    [Fact]
    public void A_squash_merged_branch_whose_remote_is_kept_is_merged_too()
    {
        Feature("feat-b");
        HostMerges("feat-b", "squash", deleteBranch: false);

        Assert.Equal(BranchFate.Merged, Check("feat-b"));
    }

    [Fact]
    public void A_branch_deleted_without_merging_is_gone()
    {
        Feature("feat-c");
        Git(_host, "push", "-q", "origin", "--delete", "feat-c");

        Assert.Equal(BranchFate.Gone, Check("feat-c"));
    }

    [Fact]
    public void A_squash_merged_branch_with_more_work_since_is_gone_not_merged()
    {
        Feature("feat-d");
        HostMerges("feat-d", "squash");
        Git(_mine, "switch", "-q", "feat-d");
        Commit(_mine, "feat-d.txt", "one\ntwo\nthree\n", "three");
        Git(_mine, "switch", "-q", "main");

        Assert.Equal(BranchFate.Gone, Check("feat-d"));
    }

    [Fact]
    public void A_pushed_branch_still_in_review_is_live()
    {
        Feature("feat-e");

        Assert.Equal(BranchFate.Live, Check("feat-e"));
    }

    [Fact]
    public void A_branch_never_pushed_is_live_with_commits_and_empty_without()
    {
        Git(_mine, "branch", "fresh", "main");
        Git(_mine, "switch", "-q", "-c", "local-work", "main");
        Commit(_mine, "local.txt", "mine\n", "mine");
        Git(_mine, "switch", "-q", "main");
        MainMovesOn();

        Assert.Equal(BranchFate.Empty, Check("fresh"));
        Assert.Equal(BranchFate.Live, Check("local-work"));
    }

    [Fact]
    public void Offline_fetch_says_so_and_checks_go_on()
    {
        Feature("feat-f");
        Git(_mine, "remote", "set-url", "origin", Path.Combine(_root, "nowhere.git"));

        Assert.False(_git.FetchPrune(_mine, TimeSpan.FromSeconds(30)));
        Assert.Equal(BranchFate.Live, _git.CheckBranch(_mine, "feat-f").Fate);
    }
}
