using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Pools;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Setup;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Pools;

/// <summary>M3: the checkout/release lifecycle over a stack's bounded pool. Real git worktrees, so it
/// runs in the git-heavy (serial) collection. The repo has no docker infra, so these focus on the pool
/// state machine (allocate under the cap, reuse, release, exhaustion); the git-refresh handling is
/// covered by <see cref="Sprig.Tests.Workspaces.WorkspaceRefreshTests"/>.</summary>
[Collection("git-heavy")]
public class PoolCheckoutTests
{
    const string Config = """{ "schema":2, "name":"app" }""";

    static PoolService Build(TempGitRepo repo, TempStore store, int maxSlots)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), Config);
        repo.Git("add", "-A");
        repo.Git("-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", "add sprig config");

        var registry = new RepoRegistryStore(store.Paths);
        registry.Add(repo.Path); // name "app" from the config

        var instances = new InstanceStore(store.Paths);
        var stacks = new StackStore(store.Paths, registry, instances);
        stacks.Save(new StackDefinition { Name = "app", Repos = ["app"], MaxSlots = maxSlots });

        var git = new GitService(new ProcessRunner());
        var workspaces = new WorkspaceService(git, new FilePortStore(store.Paths), instances,
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = true }, store.Paths,
            new SetupRunner(new ProcessRunner()));
        var resolver = new StackResolver(registry, stacks, git);
        return new PoolService(stacks, instances, resolver, workspaces, store.Paths);
    }

    [Fact]
    public void Checkout_materialises_a_new_indexed_workspace_and_cuts_the_claim_branch()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 2);

        var ws = pools.Checkout("app", existingWorkspace: null, "auth-refactor", label: "auth work");

        Assert.Equal("app-1", ws.Workspace);
        Assert.Equal("app", ws.Stack);
        Assert.Equal(1, ws.WorkspaceIndex);
        Assert.True(ws.Claimed);
        Assert.Equal("auth-refactor", ws.Branch);          // the claim branch is the identity
        Assert.Equal("auth-refactor", ws.Repos[0].Branch);  // cut per repo
        Assert.Equal("auth work", ws.Label);                // optional label kept alongside
        Assert.True(Directory.Exists(ws.Repos[0].WorktreePath));
    }

    [Fact]
    public void Checkout_with_no_label_still_claims_on_the_branch()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);

        var ws = pools.Checkout("app", null, "spike");

        Assert.True(ws.Claimed);
        Assert.Equal("spike", ws.Branch);
        Assert.Null(ws.Label); // label is optional
    }

    [Fact]
    public void Checkout_rejects_an_invalid_branch_name()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);

        // A space is not a legal git branch name — the pre-flight refuses before materialising a slot.
        Assert.ThrowsAny<Exception>(() => pools.Checkout("app", null, "auth refactor"));
        Assert.Empty(pools.Status("app").Workspaces); // nothing half-created
    }

    [Fact]
    public void Checkout_allocates_the_next_index_then_refuses_past_maxSlots()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 2);

        Assert.Equal("app-1", pools.Checkout("app", null, "one").Workspace);
        Assert.Equal("app-2", pools.Checkout("app", null, "two").Workspace);

        // The cap doing its job: a full pool refuses another.
        var ex = Assert.Throws<PoolException>(() => pools.Checkout("app", null, "three"));
        Assert.Contains("full", ex.Message);
    }

    [Fact]
    public void Release_marks_unclaimed_keeps_the_branch_and_leaves_the_worktree_on_disk()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 2);
        var ws = pools.Checkout("app", null, "wip", label: "in progress");
        var worktree = ws.Repos[0].WorktreePath;

        var (released, _) = pools.Release("app-1");

        Assert.False(released.Claimed);
        Assert.Equal("wip", released.Branch);            // branch/label kept as "last used" hints
        Assert.Equal("in progress", released.Label);
        Assert.NotNull(released.LastUsedAt);
        Assert.True(Directory.Exists(worktree));         // nothing removed from disk
    }

    [Fact]
    public void A_released_workspace_can_be_reused_without_allocating_a_new_one()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1); // room for exactly one
        pools.Checkout("app", null, "first");
        pools.Release("app-1");

        var reused = pools.Checkout("app", existingWorkspace: "app-1", "second", mode: CheckoutMode.Keep);

        Assert.Equal("app-1", reused.Workspace);
        Assert.True(reused.Claimed);
        Assert.Equal("second", reused.Branch);
        Assert.Single(pools.Status("app").Workspaces); // still just one — no new allocation
    }

    [Fact]
    public void Reusing_a_claimed_workspace_is_rejected()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 2);
        pools.Checkout("app", null, "held");

        var ex = Assert.Throws<PoolException>(() => pools.Checkout("app", "app-1", "again"));
        Assert.Contains("already claimed", ex.Message);
    }

    [Fact]
    public void Fresh_reuse_resets_the_worktree_to_base()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 1);
        var ws = pools.Checkout("app", null, "first");
        var readme = Path.Combine(ws.Repos[0].WorktreePath, "README.md");
        File.WriteAllText(readme, "local edit");
        pools.Release("app-1");

        pools.Checkout("app", "app-1", "clean-start", mode: CheckoutMode.Fresh);

        Assert.Equal("seed", File.ReadAllText(readme).Trim()); // reset to the committed base
    }

    [Fact]
    public void A_branch_name_already_used_in_the_repo_blocks_the_claim()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 2);
        pools.Checkout("app", null, "dup"); // app-1 now holds branch "dup"

        // A second workspace can't cut the same branch name — the pre-flight blocks it, naming the repo.
        var ex = Assert.ThrowsAny<Exception>(() => pools.Checkout("app", null, "dup"));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void ClaimedWorkspaces_lists_only_claimed_ones()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        var pools = Build(repo, store, maxSlots: 3);
        pools.Checkout("app", null, "a");
        pools.Checkout("app", null, "b");
        pools.Release("app-1");

        var claimed = pools.ClaimedWorkspaces("app");

        Assert.Equal(["app-2"], claimed.Select(c => c.Workspace));
    }
}
