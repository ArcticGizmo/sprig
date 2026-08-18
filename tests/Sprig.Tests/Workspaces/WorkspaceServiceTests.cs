using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

public class WorkspaceServiceTests
{
    // Zero-input repo: env references only the workspace slug, so the ad-hoc (stackless) path works.
    const string ConfigJson = """
        { "schema": 1, "name": "vue-app",
          "env": [ { "file": ".env", "set": { "NAME": "app--${sprig.workspace}" } } ] }
        """;

    static (WorkspaceService svc, InstanceStore store) Build(TempStore s)
    {
        var git = new GitService(new ProcessRunner());
        var svc = new WorkspaceService(git, new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths);
        return (svc, new InstanceStore(s.Paths));
    }

    static void SeedRepo(TempGitRepo repo)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), ConfigJson);
        File.WriteAllText(Path.Combine(repo.Path, ".env"), "NAME=original\nOTHER=keep\n");
    }

    [Fact]
    public void Create_makes_a_detached_worktree_env_and_record_with_no_branch()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances) = Build(store);

        var record = svc.Create(repo.Path, "feat-a");

        var wt = repo.SiblingWorktree("feat-a");
        Assert.True(Directory.Exists(wt));
        // A freshly-created slot is parked in detached HEAD — no branch of its own until it's claimed.
        Assert.False(new GitService(new ProcessRunner()).BranchExists(repo.Path, "sprig--feat-a"));

        var envText = File.ReadAllText(Path.Combine(wt, ".env"));
        Assert.Equal(2, envText.Split('\n').Count(l => l == "NAME=app--feat-a")); // top + bottom
        Assert.Contains("OTHER=keep", envText);

        Assert.NotNull(instances.TryLoad("feat-a"));
        Assert.Null(record.Repos[0].Branch); // parked, not claimed
    }

    [Fact]
    public void Claim_cuts_the_branch_across_the_worktree()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        svc.Create(repo.Path, "feat-a");

        var claimed = svc.Claim("feat-a", "feat-a-work", fresh: false);

        Assert.Equal("feat-a-work", claimed.Branch);
        Assert.Equal("feat-a-work", claimed.Repos[0].Branch);
        Assert.True(new GitService(new ProcessRunner()).BranchExists(repo.Path, "feat-a-work"));
    }

    [Fact]
    public void Claim_refuses_a_branch_name_that_already_exists()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        svc.Create(repo.Path, "feat-a");
        svc.Claim("feat-a", "taken", fresh: false);

        // Same repo, second workspace, same branch name — blocked, naming the repo.
        svc.Create(repo.Path, "feat-b");
        var ex = Assert.Throws<WorkspaceException>(() => svc.Claim("feat-b", "taken", fresh: false));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void Two_workspaces_get_separate_worktrees()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);

        svc.Create(repo.Path, "feat-a");
        svc.Create(repo.Path, "feat-b");

        Assert.True(Directory.Exists(repo.SiblingWorktree("feat-a")));
        Assert.True(Directory.Exists(repo.SiblingWorktree("feat-b")));
    }

    [Fact]
    public void Create_rejects_duplicate_workspace()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        svc.Create(repo.Path, "dupe");
        Assert.Throws<WorkspaceException>(() => svc.Create(repo.Path, "dupe"));
    }

    [Fact]
    public void Create_rejects_invalid_name()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        Assert.Throws<WorkspaceException>(() => svc.Create(repo.Path, "bad name/slash"));
    }

    [Fact]
    public void Create_rejects_non_git_path()
    {
        using var store = new TempStore();
        var (svc, _) = Build(store);
        var plain = Directory.CreateTempSubdirectory("sprig-notgit-");
        try { Assert.Throws<WorkspaceException>(() => svc.Create(plain.FullName, "x")); }
        finally { plain.Delete(recursive: true); }
    }

    [Fact]
    public void Create_rolls_back_when_config_missing()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo(); // no .sprig.json
        var (svc, instances) = Build(store);

        Assert.ThrowsAny<Exception>(() => svc.Create(repo.Path, "feat-a"));
        Assert.Null(instances.TryLoad("feat-a"));
        Assert.False(Directory.Exists(repo.SiblingWorktree("feat-a")));
    }

    [Fact]
    public void Remove_deletes_worktree_keeps_branch_by_default()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances) = Build(store);
        svc.Create(repo.Path, "feat-a");
        svc.Claim("feat-a", "feat-a-work", fresh: false); // cut a branch so there's one to keep

        svc.Remove("feat-a");

        Assert.False(Directory.Exists(repo.SiblingWorktree("feat-a")));
        Assert.Null(instances.TryLoad("feat-a"));
        Assert.True(new GitService(new ProcessRunner()).BranchExists(repo.Path, "feat-a-work"));
    }

    [Fact]
    public void Remove_with_force_deletes_branch()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        svc.Create(repo.Path, "feat-a");
        svc.Claim("feat-a", "feat-a-work", fresh: false);

        svc.Remove("feat-a", force: true);

        Assert.False(new GitService(new ProcessRunner()).BranchExists(repo.Path, "feat-a-work"));
    }

    [Fact]
    public void Remove_tolerates_manually_deleted_folder_and_is_idempotent()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances) = Build(store);
        svc.Create(repo.Path, "feat-a");

        WorktreeInspectorProxy.DeleteDir(repo.SiblingWorktree("feat-a"));
        svc.Remove("feat-a");
        Assert.Null(instances.TryLoad("feat-a"));

        svc.Remove("feat-a"); // no throw
    }

    [Fact]
    public void Remove_of_unknown_workspace_is_noop()
    {
        using var store = new TempStore();
        var (svc, _) = Build(store);
        svc.Remove("never-existed");
    }
}

/// <summary>Tiny helper so tests can force-delete a folder with the same lock-retry as the Core.</summary>
static class WorktreeInspectorProxy
{
    public static void DeleteDir(string path)
    {
        for (var i = 0; i < 8 && Directory.Exists(path); i++)
        {
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) { Thread.Sleep(20); }
        }
    }
}
