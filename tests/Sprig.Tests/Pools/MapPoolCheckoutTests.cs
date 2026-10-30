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

namespace Sprig.Tests.Pools;

/// <summary>F1 — the checkout/release lifecycle over a map's bounded pool (<see cref="MapPoolService"/>).
/// Real git worktrees, so it runs in the git-heavy (serial) collection. The map's repo has no docker infra,
/// so these focus on the pool state machine: allocate under the cap, reuse, release, exhaustion — the
/// map-model mirror of <see cref="PoolCheckoutTests"/>.</summary>
[Collection("git-heavy")]
public class MapPoolCheckoutTests
{
    const string Config = """{ "schema":1, "name":"app" }""";

    static MapPoolService Build(TempGitRepo repo, TempStore store, int maxSlots)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), Config);
        repo.Git("add", "-A");
        repo.Git("-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", "add sprig config");

        var registry = new RepoRegistryStore(store.Paths);
        registry.Add(repo.Path); // name "app" from the config

        var instances = new InstanceStore(store.Paths);
        var maps = new MapStore(store.Paths, registry);
        maps.Save(new MapDefinition { Name = "app", Repos = [MapRepo.Local("app")], MaxSlots = maxSlots });

        var git = new GitService(new ProcessRunner());
        var workspaces = new WorkspaceService(git, new FilePortStore(store.Paths), instances,
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = true }, store.Paths,
            new SetupRunner(new ProcessRunner()));
        var resolver = new MapResolver(registry, maps, git, store.Paths);
        return new MapPoolService(maps, instances, resolver, workspaces, store.Paths);
    }

    [Fact]
    public void Checkout_materialises_a_new_indexed_workspace_and_cuts_the_claim_branch()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 2);

        var ws = pools.Checkout("app", existingWorkspace: null, "auth-refactor", label: "auth work");

        Assert.Equal("app-1", ws.Workspace);
        Assert.Equal("app", ws.Map);                       // tagged with the map, not a stack
        Assert.Null(ws.Stack);
        Assert.Equal(1, ws.WorkspaceIndex);
        Assert.True(ws.Claimed);
        Assert.Equal("auth-refactor", ws.Branch);          // recorded as the claim branch
        Assert.Equal("auth-refactor", ws.Repos[0].Branch);  // cut per repo
        Assert.Equal("auth work", ws.Label);
        Assert.True(Directory.Exists(ws.Repos[0].WorktreePath));
    }

    [Fact]
    public void Checkout_rejects_an_invalid_branch_name_without_materialising()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);

        Assert.ThrowsAny<Exception>(() => pools.Checkout("app", null, "auth refactor"));
        Assert.Empty(pools.Status("app").Workspaces); // nothing half-created
    }

    [Fact]
    public void Checkout_allocates_the_next_index_then_refuses_past_maxSlots()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 2);

        var a = pools.Checkout("app", null, "one");
        var b = pools.Checkout("app", null, "two");
        Assert.Equal(["app-1", "app-2"], new[] { a.Workspace, b.Workspace });

        // The pool is full (2/2 claimed) — a third --new checkout is refused.
        var ex = Assert.Throws<PoolException>(() => pools.Checkout("app", null, "three"));
        Assert.Contains("full", ex.Message);
    }

    [Fact]
    public void Release_frees_a_claimed_workspace_which_a_later_checkout_can_reuse()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);

        var first = pools.Checkout("app", null, "first-branch");
        var (released, _) = pools.Release(first.Workspace);
        Assert.False(released.Claimed);

        // Reuse the freed workspace (the only member) on a new branch.
        var reused = pools.Checkout("app", existingWorkspace: released.Workspace, "second-branch");
        Assert.Equal(first.Workspace, reused.Workspace);
        Assert.True(reused.Claimed);
        Assert.Equal("second-branch", reused.Branch);
    }

    [Fact]
    public void Tags_ride_the_claim_and_move_to_ReleasedTags_on_release()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);
        var tags = new Dictionary<string, string> { ["agent.session"] = "7f3a" };

        var ws = pools.Checkout("app", null, "tagged", tags: tags);
        Assert.Equal("7f3a", ws.Tags["agent.session"]);

        var (released, _) = pools.Release(ws.Workspace);
        Assert.Empty(released.Tags);
        Assert.Equal("7f3a", released.ReleasedTags["agent.session"]);
    }

    [Fact]
    public void Label_and_note_go_with_the_claim_and_the_next_claimer_starts_without_them()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);
        var instances = new InstanceStore(store.Paths);

        var ws = pools.Checkout("app", null, "first", label: "Audit log");
        instances.Save(instances.TryLoad(ws.Workspace)! with { Note = "waiting on review" });

        var (released, _) = pools.Release(ws.Workspace);
        Assert.Null(released.Label);
        Assert.Null(released.Note);
        Assert.Equal("Audit log", released.ReleasedLabel);
        Assert.Equal("waiting on review", released.ReleasedNote);

        // Even a free record that somehow still holds a label and note doesn't pass them on.
        instances.Save(released with { Label = "stale", Note = "stale" });
        var reclaimed = pools.Checkout("app", existingWorkspace: ws.Workspace, "second");
        Assert.Null(reclaimed.Label);
        Assert.Null(reclaimed.Note);
    }

    [Fact]
    public void An_invalid_tag_key_is_rejected_before_materialising()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);

        Assert.Throws<WorkspaceException>(() =>
            pools.Checkout("app", null, "x", tags: new Dictionary<string, string> { ["Bad Key"] = "v" }));
        Assert.Empty(pools.Status("app").Workspaces);
    }

    [Fact]
    public void Detached_checkout_claims_a_new_workspace_with_no_branch()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);

        var ws = pools.Checkout("app", null, branch: null, label: "review PR 41");

        Assert.True(ws.Claimed);
        Assert.Null(ws.Branch);
        Assert.Null(ws.Repos[0].Branch);
        Assert.Equal("review PR 41", ws.Label);
        Assert.True(WorktreeHead.Read(ws.Repos[0].WorktreePath)!.IsDetached);
    }

    [Fact]
    public void Detached_reuse_leaves_the_previous_branch_in_place()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);

        var first = pools.Checkout("app", null, "first-branch");
        pools.Release(first.Workspace);
        var reused = pools.Checkout("app", existingWorkspace: first.Workspace, branch: null, startPoint: "main");

        Assert.Null(reused.Branch);
        Assert.True(WorktreeHead.Read(reused.Repos[0].WorktreePath)!.IsDetached);
        Assert.Contains("first-branch", repo.Git("branch", "--list", "first-branch").StdOut);
    }
}
