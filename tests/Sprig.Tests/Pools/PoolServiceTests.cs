using Sprig.Core.Pools;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

namespace Sprig.Tests.Pools;

/// <summary>M2: the pool is derived from the instance store, not persisted — <see cref="PoolService"/>
/// reports a stack's workspaces and its <c>maxSlots</c> ceiling. No git or docker involved.</summary>
public class PoolServiceTests
{
    static (PoolService pools, InstanceStore instances) Build(TempStore s, int maxSlots = 4)
    {
        var registry = new RepoRegistryStore(s.Paths);
        var dir = Path.Combine(s.Root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":2, "name":"app" }""");
        registry.Add(dir);

        var instances = new InstanceStore(s.Paths);
        var stacks = new StackStore(s.Paths, registry, instances);
        stacks.Save(new StackDefinition { Name = "app", Repos = ["app"], MaxSlots = maxSlots });
        return (new PoolService(stacks, instances), instances);
    }

    static InstanceRecord Workspace(string name, int index, bool claimed, string? label = null) => new()
    {
        Workspace = name,
        Stack = "app",
        WorkspaceIndex = index,
        Claimed = claimed,
        Label = label,
    };

    [Fact]
    public void Status_of_a_fresh_stack_is_empty_with_the_ceiling()
    {
        using var s = new TempStore();
        var (pools, _) = Build(s, maxSlots: 4);

        var status = pools.Status("app");

        Assert.Equal("app", status.Stack);
        Assert.Equal(4, status.MaxSlots);
        Assert.Empty(status.Workspaces);
        Assert.Equal(0, status.ClaimedCount);
        Assert.Equal(4, status.Headroom);
        Assert.False(status.IsExhausted);
    }

    [Fact]
    public void Status_lists_pool_workspaces_ordered_by_index_and_counts_claimed()
    {
        using var s = new TempStore();
        var (pools, instances) = Build(s, maxSlots: 4);
        instances.Save(Workspace("app-2", 2, claimed: false));
        instances.Save(Workspace("app-1", 1, claimed: true, label: "auth"));

        var status = pools.Status("app");

        Assert.Equal(["app-1", "app-2"], status.Workspaces.Select(w => w.Workspace));
        Assert.Equal(1, status.ClaimedCount);
        Assert.Equal(1, status.FreeCount);
        Assert.Equal(2, status.Headroom); // 4 - 2 built
        Assert.False(status.IsExhausted); // a free one exists
    }

    [Fact]
    public void A_full_pool_with_no_free_workspaces_is_exhausted()
    {
        using var s = new TempStore();
        var (pools, instances) = Build(s, maxSlots: 2);
        instances.Save(Workspace("app-1", 1, claimed: true));
        instances.Save(Workspace("app-2", 2, claimed: true));

        var status = pools.Status("app");

        Assert.Equal(0, status.FreeCount);
        Assert.Equal(0, status.Headroom);
        Assert.True(status.IsExhausted);
    }

    [Fact]
    public void Status_of_an_unknown_stack_throws()
    {
        using var s = new TempStore();
        var (pools, _) = Build(s);
        Assert.Throws<StackException>(() => pools.Status("ghost"));
    }
}
