using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

public class WorkspaceReconcilerTests
{
    // ---- deterministic classification via a fake git + controlled folders ----

    static InstanceRecord RecordFor(string ws, string source, string worktree) => new()
    {
        Workspace = ws,
        Repos = [new InstanceRepo { Name = "r", SourcePath = source, WorktreePath = worktree, Branch = $"sprig/{ws}" }],
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Theory]
    [InlineData(true, true, WorktreeState.Healthy)]
    [InlineData(true, false, WorktreeState.MissingFolder)]
    [InlineData(false, true, WorktreeState.Orphaned)]
    [InlineData(false, false, WorktreeState.Gone)]
    public void Classifies_all_four_states(bool registered, bool folderExists, WorktreeState expected)
    {
        using var s = new TempStore();
        var folder = Path.Combine(s.Root, "wt");
        if (folderExists) Directory.CreateDirectory(folder);

        var fake = new FakeGitService();
        if (registered) fake.Worktrees.Add(new WorktreeInfo(folder, "h", "sprig/ws", false));

        var instances = new InstanceStore(s.Paths);
        instances.Save(RecordFor("ws", @"C:\src", folder));

        var report = new WorkspaceReconciler(fake, instances).Inspect("ws");

        Assert.NotNull(report);
        Assert.Equal(expected, report!.Repos[0].State);
        Assert.Equal(expected == WorktreeState.Healthy, report.IsHealthy);
    }

    [Fact]
    public void Inspect_unknown_workspace_is_null()
    {
        using var s = new TempStore();
        Assert.Null(new WorkspaceReconciler(new FakeGitService(), new InstanceStore(s.Paths)).Inspect("nope"));
    }

    [Fact]
    public void InspectAll_covers_every_record()
    {
        using var s = new TempStore();
        var instances = new InstanceStore(s.Paths);
        instances.Save(RecordFor("a", @"C:\src", Path.Combine(s.Root, "a")));
        instances.Save(RecordFor("b", @"C:\src", Path.Combine(s.Root, "b")));

        var all = new WorkspaceReconciler(new FakeGitService { RepoExists = false }, instances).InspectAll();
        Assert.Equal(["a", "b"], all.Select(r => r.Workspace).Order());
    }

    // ---- real repair against actual git ----

    static WorkspaceService BuildService(TempStore s) =>
        new(new GitService(new ProcessRunner()), new FilePortStore(s.Paths),
            new InstanceStore(s.Paths), new EnvClobberService(),
            new Sprig.Core.Compose.ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths);

    static void SeedRepo(TempGitRepo repo)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), """{ "schema":1, "name":"r" }""");
    }

    [Fact]
    public void Repairs_drift_A_deleted_folder_by_pruning()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        BuildService(store).Create(repo.Path, "feat");

        // Drift A: delete the worktree folder manually.
        Directory.Delete(repo.SiblingWorktree("feat"), recursive: true);

        var git = new GitService(new ProcessRunner());
        var reconciler = new WorkspaceReconciler(git, new InstanceStore(store.Paths));

        Assert.Equal(WorktreeState.MissingFolder, reconciler.Inspect("feat")!.Repos[0].State);
        var actions = reconciler.Repair("feat");
        Assert.Contains(actions, a => a.Contains("pruned"));
        Assert.Equal(WorktreeState.Gone, reconciler.Inspect("feat")!.Repos[0].State);
    }

    [Fact]
    public void Repairs_drift_B_orphan_folder_by_removing_it()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        BuildService(store).Create(repo.Path, "feat");
        var wt = repo.SiblingWorktree("feat");

        // Drift B: unregister the worktree from git but leave the folder on disk.
        new GitService(new ProcessRunner()).RemoveWorktree(repo.Path, wt);
        Directory.CreateDirectory(wt);
        File.WriteAllText(Path.Combine(wt, "leftover.txt"), "x");

        var reconciler = new WorkspaceReconciler(new GitService(new ProcessRunner()), new InstanceStore(store.Paths));
        Assert.Equal(WorktreeState.Orphaned, reconciler.Inspect("feat")!.Repos[0].State);

        var actions = reconciler.Repair("feat");
        Assert.Contains(actions, a => a.Contains("orphan"));
        Assert.False(Directory.Exists(wt));
    }
}
