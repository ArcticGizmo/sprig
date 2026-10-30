using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Maps;
using Sprig.Core.Pools;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Setup;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary><c>ws switch</c> against real worktrees: the workspace moves on, the branch it leaves stays put,
/// and anything that would block is caught before any worktree moves.</summary>
[Collection("git-heavy")]
public class WorkspaceSwitchTests
{
    // A map named "app" over every repo given, each registered under its folder name.
    static (MapPoolService pools, WorkspaceService workspaces) Build(TempStore store, params TempGitRepo[] repos)
    {
        var registry = new RepoRegistryStore(store.Paths);
        foreach (var repo in repos)
        {
            File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), $$"""{ "schema":1, "name":"{{Path.GetFileName(repo.Path)}}" }""");
            repo.Git("add", "-A");
            repo.Git("-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", "add sprig config");
            registry.Add(repo.Path);
        }

        var instances = new InstanceStore(store.Paths);
        var maps = new MapStore(store.Paths, registry);
        maps.Save(new MapDefinition { Name = "app", Repos = [.. repos.Select(r => MapRepo.Local(Path.GetFileName(r.Path)))], MaxSlots = 2 });

        var git = new GitService(new ProcessRunner());
        var workspaces = new WorkspaceService(git, new FilePortStore(store.Paths), instances,
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = true }, store.Paths,
            new SetupRunner(new ProcessRunner()));
        return (new MapPoolService(maps, instances, new MapResolver(registry, maps, git, store.Paths), workspaces, store.Paths), workspaces);
    }

    static void Commit(string worktree, string file)
    {
        File.WriteAllText(Path.Combine(worktree, file), file);
        var runner = new ProcessRunner();
        runner.Run("git", ["-C", worktree, "add", "-A"], worktree).EnsureSuccess();
        runner.Run("git", ["-C", worktree, "-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", file], worktree).EnsureSuccess();
    }

    static string? Head(InstanceRecord r) => WorktreeHead.Read(r.Repos[0].WorktreePath)!.Branch;

    [Fact]
    public void New_branch_moves_on_and_leaves_the_old_branch_with_its_commits()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, workspaces) = Build(store, repo);
        var ws = pools.Checkout("app", null, "first", label: "Audit log", tags: new Dictionary<string, string> { ["agent.session"] = "1" });
        workspaces.SetNote(ws.Workspace, "waiting on review");
        Commit(ws.Repos[0].WorktreePath, "work.txt");

        var result = workspaces.Switch(ws.Workspace, SwitchTarget.NewBranch("second", "main"));

        Assert.Equal("second", Head(result.Record));
        Assert.Equal("second", result.Record.Branch);
        Assert.False(File.Exists(Path.Combine(ws.Repos[0].WorktreePath, "work.txt"))); // started from main
        Assert.Contains("first", repo.Git("branch", "--list", "first").StdOut);         // old branch kept
        Assert.Contains("work.txt", repo.Git("show", "--name-only", "first").StdOut);    // with its commit
        Assert.True(result.Previous.Repos[0].UnpushedCommits > 0);                      // and reported
        Assert.Equal("Audit log", result.Record.Label);                                 // purpose untouched
        Assert.Equal("waiting on review", result.Record.Note);
        Assert.Equal("1", result.Record.Tags["agent.session"]);
    }

    [Fact]
    public void Purpose_survives_checking_out_a_branch_and_detaching()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, workspaces) = Build(store, repo);
        repo.Git("branch", "hotfix");
        var ws = pools.Checkout("app", null, "first", label: "Audit log", tags: new Dictionary<string, string> { ["agent.session"] = "1" });
        workspaces.SetNote(ws.Workspace, "waiting on review");

        foreach (var target in new[] { SwitchTarget.ExistingBranch("hotfix"), SwitchTarget.Detach("main") })
        {
            var record = workspaces.Switch(ws.Workspace, target).Record;
            Assert.Equal("Audit log", record.Label);
            Assert.Equal("waiting on review", record.Note);
            Assert.Equal("1", record.Tags["agent.session"]);
            Assert.True(record.Claimed);
        }
    }

    [Fact]
    public void Only_moves_the_named_repos_and_checks_nothing_else()
    {
        using var store = new TempStore();
        using var api = new TempGitRepo("api");
        using var web = new TempGitRepo("web");
        var (pools, workspaces) = Build(store, api, web);
        var ws = pools.Checkout("app", null, "first");
        var apiPath = ws.Repos.Single(r => r.Name == "api").WorktreePath;
        File.WriteAllText(Path.Combine(apiPath, "scratch.txt"), "wip"); // dirty, but not being switched

        var result = workspaces.Switch(ws.Workspace, SwitchTarget.NewBranch("second"), onlyRepos: ["web"]);

        var repos = result.Record.Repos.ToDictionary(r => r.Name);
        Assert.Equal("first", WorktreeHead.Read(repos["api"].WorktreePath)!.Branch);
        Assert.Equal("first", repos["api"].Branch);
        Assert.Equal("second", WorktreeHead.Read(repos["web"].WorktreePath)!.Branch);
        Assert.Equal("second", repos["web"].Branch);
        Assert.Equal("second", result.Record.Branch);                         // the claim branch follows the switch
        Assert.Equal("", api.Git("branch", "--list", "second").StdOut.Trim()); // api never got the branch
        Assert.True(File.Exists(Path.Combine(apiPath, "scratch.txt")));
    }

    [Fact]
    public void Existing_branch_is_checked_out()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, workspaces) = Build(store, repo);
        repo.Git("branch", "hotfix");
        var ws = pools.Checkout("app", null, "first");

        Assert.Equal("hotfix", Head(workspaces.Switch(ws.Workspace, SwitchTarget.ExistingBranch("hotfix")).Record));
    }

    [Fact]
    public void Existing_branch_held_by_another_workspace_names_it()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, workspaces) = Build(store, repo);
        var a = pools.Checkout("app", null, "first");
        var b = pools.Checkout("app", null, "second");

        var ex = Assert.Throws<WorkspaceException>(() => workspaces.Switch(b.Workspace, SwitchTarget.ExistingBranch("first")));
        Assert.Contains($"workspace '{a.Workspace}'", ex.Message);
        Assert.Equal("second", Head(workspaces.Get(b.Workspace)!)); // nothing moved
    }

    [Fact]
    public void Missing_branch_and_taken_name_are_refused()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, workspaces) = Build(store, repo);
        var ws = pools.Checkout("app", null, "first");

        Assert.Contains("no branch 'ghost'",
            Assert.Throws<WorkspaceException>(() => workspaces.Switch(ws.Workspace, SwitchTarget.ExistingBranch("ghost"))).Message);
        Assert.Contains("already exists",
            Assert.Throws<WorkspaceException>(() => workspaces.Switch(ws.Workspace, SwitchTarget.NewBranch("main"))).Message);
        Assert.Contains("no ref 'nope'",
            Assert.Throws<WorkspaceException>(() => workspaces.Switch(ws.Workspace, SwitchTarget.Detach("nope"))).Message);
    }

    [Fact]
    public void Uncommitted_changes_block_the_switch()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, workspaces) = Build(store, repo);
        var ws = pools.Checkout("app", null, "first");
        File.WriteAllText(Path.Combine(ws.Repos[0].WorktreePath, "scratch.txt"), "wip");

        var ex = Assert.Throws<WorkspaceException>(() => workspaces.Switch(ws.Workspace, SwitchTarget.NewBranch("second")));
        Assert.Contains("uncommitted", ex.Message);
        Assert.Equal("first", Head(workspaces.Get(ws.Workspace)!));
    }

    [Fact]
    public void A_branch_only_on_remotes_is_tracked_from_origin_even_when_upstream_has_it_too()
    {
        using var origin = new TempGitRepo("origin");
        using var upstream = new TempGitRepo("upstream");
        using var repo = new TempGitRepo();
        origin.Git("branch", "feat-x");
        upstream.Git("branch", "feat-x");
        repo.Git("remote", "add", "origin", origin.Path);
        repo.Git("remote", "add", "upstream", upstream.Path);
        repo.Git("fetch", "--all");

        new GitService(new ProcessRunner()).SwitchToBranch(repo.Path, "feat-x");

        Assert.Equal("feat-x", WorktreeHead.Read(repo.Path)!.Branch);
        Assert.Equal("origin/feat-x", repo.Git("rev-parse", "--abbrev-ref", "feat-x@{upstream}").StdOut.Trim());
    }

    [Fact]
    public void Detach_parks_with_no_claim_branch()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var (pools, workspaces) = Build(store, repo);
        var ws = pools.Checkout("app", null, "first");

        var result = workspaces.Switch(ws.Workspace, SwitchTarget.Detach("main"));
        Assert.True(WorktreeHead.Read(result.Record.Repos[0].WorktreePath)!.IsDetached);
        Assert.Null(result.Record.Branch);
        Assert.Contains("first", repo.Git("branch", "--list", "first").StdOut);
    }
}
