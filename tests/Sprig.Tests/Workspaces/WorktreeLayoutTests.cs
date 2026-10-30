using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Settings;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary>The Claude worktree layout: the real worktree nests under <c>.claude/worktrees</c> (where Claude Code
/// will enter it), the sibling path is a link to it, and nothing sprig deletes ever follows that link.</summary>
public class WorktreeLayoutTests
{
    static WorkspaceService Build(TempStore s, WorktreeLayout layout)
    {
        var settings = new FileSettingsStore(s.Paths);
        var stored = settings.Get();
        stored.WorktreeLayout = layout;
        settings.Save(stored);
        return new WorkspaceService(new GitService(new ProcessRunner()), new FilePortStore(s.Paths),
            new InstanceStore(s.Paths), new EnvClobberService(), new ComposeGenerator(),
            new FakeDockerService { Available = false }, s.Paths, settings: settings);
    }

    static void SeedRepo(TempGitRepo repo)
        => File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), """{ "schema":1, "name":"r" }""");

    static string Nested(TempGitRepo repo, string ws) => Path.Combine(repo.Path, ".claude", "worktrees", ws);

    [Fact]
    public void Claude_layout_nests_the_worktree_and_links_the_sibling_path_to_it()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);

        var record = Build(store, WorktreeLayout.Claude).Create(repo.Path, "feat");

        var nested = Nested(repo, "feat");
        var sibling = repo.SiblingWorktree("feat");
        Assert.Equal(nested, record.Repos[0].WorktreePath);
        Assert.Equal(sibling, record.Repos[0].LinkPath);
        Assert.True(File.Exists(Path.Combine(nested, "README.md")));
        Assert.True(DirectoryLink.PointsTo(sibling, nested));
        Assert.True(File.Exists(Path.Combine(sibling, "README.md")));
    }

    [Fact]
    public void Claude_layout_hides_the_nested_worktrees_from_the_source_repo()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);

        Build(store, WorktreeLayout.Claude).Create(repo.Path, "feat");

        Assert.Contains(".claude/worktrees/", File.ReadAllLines(Path.Combine(repo.Path, ".git", "info", "exclude")));
        Assert.DoesNotContain(".claude", repo.Git("status", "--porcelain").StdOut);
    }

    [Fact]
    public void Sibling_layout_is_the_default_and_makes_no_link()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);

        var record = Build(store, WorktreeLayout.Sibling).Create(repo.Path, "feat");

        Assert.Equal(repo.SiblingWorktree("feat"), record.Repos[0].WorktreePath);
        Assert.Null(record.Repos[0].LinkPath);
        Assert.False(DirectoryLink.IsLink(repo.SiblingWorktree("feat")));
        Assert.Equal(WorktreeLayout.Sibling, new SprigSettings().WorktreeLayout);
    }

    [Fact]
    public void Remove_takes_the_link_and_the_worktree()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var svc = Build(store, WorktreeLayout.Claude);
        svc.Create(repo.Path, "feat");

        svc.Remove("feat");

        Assert.False(DirectoryLink.Occupied(repo.SiblingWorktree("feat")));
        Assert.False(Directory.Exists(Nested(repo, "feat")));
        Assert.Null(svc.Get("feat"));
        Assert.True(File.Exists(Path.Combine(repo.Path, "README.md")));
    }

    [Fact]
    public void Remove_leaves_a_real_folder_that_replaced_the_link()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var svc = Build(store, WorktreeLayout.Claude);
        svc.Create(repo.Path, "feat");
        var sibling = repo.SiblingWorktree("feat");
        DirectoryLink.Remove(sibling);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "mine.txt"), "keep me");

        svc.Remove("feat");

        Assert.True(File.Exists(Path.Combine(sibling, "mine.txt")));
        Assert.False(Directory.Exists(Nested(repo, "feat")));
    }

    [Fact]
    public void Create_refuses_when_the_link_path_is_taken()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        Directory.CreateDirectory(repo.SiblingWorktree("feat"));

        var ex = Assert.Throws<WorkspaceException>(() => Build(store, WorktreeLayout.Claude).Create(repo.Path, "feat"));

        Assert.Contains("shortcut", ex.Message);
        Assert.False(Directory.Exists(Nested(repo, "feat")));
    }

    [Fact]
    public void Here_resolves_a_directory_reached_through_the_link()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        var svc = Build(store, WorktreeLayout.Claude);
        svc.Create(repo.Path, "feat");

        var viaLink = svc.Locate(repo.SiblingWorktree("feat"));
        var viaReal = svc.Locate(Nested(repo, "feat"));

        Assert.Equal("feat", viaLink?.Record.Workspace);
        Assert.Equal("feat", viaReal?.Record.Workspace);
    }

    [Fact]
    public void Reconcile_reports_and_recreates_a_missing_link()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        Build(store, WorktreeLayout.Claude).Create(repo.Path, "feat");
        var reconciler = new WorkspaceReconciler(new GitService(new ProcessRunner()), new InstanceStore(store.Paths));
        Assert.True(reconciler.Inspect("feat")!.IsHealthy);

        DirectoryLink.Remove(repo.SiblingWorktree("feat"));
        var report = reconciler.Inspect("feat")!;
        Assert.Equal(LinkState.Missing, report.Repos[0].Link);
        Assert.True(report.HasDrift);

        var actions = reconciler.Repair("feat");

        Assert.Contains(actions, a => a.Contains("recreated"));
        Assert.Equal(LinkState.Ok, reconciler.Inspect("feat")!.Repos[0].Link);
    }

    [Fact]
    public void Reconcile_never_touches_a_real_folder_at_the_link_path()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        SeedRepo(repo);
        Build(store, WorktreeLayout.Claude).Create(repo.Path, "feat");
        var sibling = repo.SiblingWorktree("feat");
        DirectoryLink.Remove(sibling);
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "mine.txt"), "keep me");
        var reconciler = new WorkspaceReconciler(new GitService(new ProcessRunner()), new InstanceStore(store.Paths));

        Assert.Equal(LinkState.Blocked, reconciler.Inspect("feat")!.Repos[0].Link);
        reconciler.Repair("feat");

        Assert.True(File.Exists(Path.Combine(sibling, "mine.txt")));
        Assert.False(DirectoryLink.IsLink(sibling));
    }

    [Fact]
    public void Layout_round_trips_through_the_settings_file()
    {
        using var store = new TempStore();
        var settings = new FileSettingsStore(store.Paths);
        var s = settings.Get();
        s.WorktreeLayout = WorktreeLayout.Claude;
        settings.Save(s);

        Assert.Equal(WorktreeLayout.Claude, settings.Get().WorktreeLayout);
        Assert.Contains("\"Claude\"", File.ReadAllText(store.Paths.SettingsFile));
        Assert.Equal(WorktreeLayout.Claude, s.Clone().WorktreeLayout);
    }
}

/// <summary>Links must never take their target down with them.</summary>
public class DirectoryLinkTests
{
    [Fact]
    public void A_link_shows_its_target_and_removing_it_keeps_the_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "a.txt"), "a");
        try
        {
            DirectoryLink.Create(link, target);

            Assert.True(DirectoryLink.IsLink(link));
            Assert.True(DirectoryLink.PointsTo(link, target));
            Assert.Equal("a", File.ReadAllText(Path.Combine(link, "a.txt")));

            Assert.True(DirectoryLink.Remove(link));
            Assert.False(DirectoryLink.Occupied(link));
            Assert.True(File.Exists(Path.Combine(target, "a.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Remove_refuses_a_real_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sprig-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        try
        {
            Assert.False(DirectoryLink.IsLink(dir));
            Assert.False(DirectoryLink.Remove(dir));
            Assert.True(File.Exists(Path.Combine(dir, "a.txt")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_dangling_link_is_still_a_link()
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            DirectoryLink.Create(link, target);
            Directory.Delete(target);

            Assert.True(DirectoryLink.Occupied(link));
            Assert.True(DirectoryLink.IsLink(link));
            Assert.True(DirectoryLink.Remove(link));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
