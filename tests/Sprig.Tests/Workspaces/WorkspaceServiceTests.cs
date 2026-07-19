using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

public class WorkspaceServiceTests
{
    const string ConfigJson = """
        { "schema": 1, "name": "vue-app",
          "ports": [ { "name": "frontend" } ],
          "env": [ { "file": ".env", "set": { "PORT": "${sprig.ports.frontend}" } } ] }
        """;

    static (WorkspaceService svc, InstanceStore store) Build(TempStore s)
    {
        var git = new GitService(new ProcessRunner());
        var ports = new FilePortStore(s.Paths);
        var instances = new InstanceStore(s.Paths);
        var svc = new WorkspaceService(git, ports, instances, new EnvClobberService(),
            new Sprig.Core.Compose.ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths);
        return (svc, instances);
    }

    static void SeedRepo(TempGitRepo repo)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), ConfigJson);
        File.WriteAllText(Path.Combine(repo.Path, ".env"), "PORT=6010\nOTHER=keep\n");
    }

    [Fact]
    public void Create_makes_worktree_branch_env_and_record()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances) = Build(store);

        var record = svc.Create(repo.Path, "feat-a");

        var wt = repo.SiblingWorktree("feat-a");
        Assert.True(Directory.Exists(wt));

        var git = new GitService(new ProcessRunner());
        Assert.True(git.BranchExists(repo.Path, "sprig/feat-a"));

        var envText = File.ReadAllText(Path.Combine(wt, ".env"));
        var port = record.Repos[0].Ports["frontend"];
        Assert.Equal(2, envText.Split('\n').Count(l => l == $"PORT={port}")); // top + bottom
        Assert.Contains("OTHER=keep", envText);                                // seeded content preserved

        Assert.NotNull(instances.TryLoad("feat-a"));
        Assert.Equal("sprig/feat-a", record.Repos[0].Branch);
    }

    [Fact]
    public void Two_workspaces_get_non_colliding_ports()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);

        var a = svc.Create(repo.Path, "feat-a");
        var b = svc.Create(repo.Path, "feat-b");

        Assert.NotEqual(a.Repos[0].Ports["frontend"], b.Repos[0].Ports["frontend"]);
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
        using var repo = new TempGitRepo(); // no .sprig.json written
        var (svc, instances) = Build(store);

        Assert.ThrowsAny<Exception>(() => svc.Create(repo.Path, "feat-a"));
        Assert.Null(instances.TryLoad("feat-a"));
        Assert.False(Directory.Exists(repo.SiblingWorktree("feat-a")));
        Assert.Null(new FilePortStore(store.Paths).Peek("feat-a")); // ports released
    }

    [Fact]
    public void Remove_deletes_worktree_keeps_branch_by_default()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances) = Build(store);
        svc.Create(repo.Path, "feat-a");

        svc.Remove("feat-a");

        Assert.False(Directory.Exists(repo.SiblingWorktree("feat-a")));
        Assert.Null(instances.TryLoad("feat-a"));
        Assert.True(new GitService(new ProcessRunner()).BranchExists(repo.Path, "sprig/feat-a")); // kept
        Assert.Null(new FilePortStore(store.Paths).Peek("feat-a"));                                // released
    }

    [Fact]
    public void Remove_with_force_deletes_branch()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, _) = Build(store);
        svc.Create(repo.Path, "feat-a");

        svc.Remove("feat-a", force: true);

        Assert.False(new GitService(new ProcessRunner()).BranchExists(repo.Path, "sprig/feat-a"));
    }

    [Fact]
    public void Remove_tolerates_manually_deleted_folder_and_is_idempotent()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var (svc, instances) = Build(store);
        svc.Create(repo.Path, "feat-a");

        // Drift A: delete the worktree folder out from under git, then tear down.
        WorktreeInspectorProxy.DeleteDir(repo.SiblingWorktree("feat-a"));
        svc.Remove("feat-a");
        Assert.Null(instances.TryLoad("feat-a"));

        // Second teardown is a no-op, not a throw.
        svc.Remove("feat-a");
    }

    [Fact]
    public void Remove_of_unknown_workspace_is_noop()
    {
        using var store = new TempStore();
        var (svc, _) = Build(store);
        svc.Remove("never-existed"); // no throw
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
