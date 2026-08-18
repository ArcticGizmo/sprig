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

/// <summary>F1 — the map-model pool (<see cref="MapPoolService"/>): membership by
/// <see cref="InstanceRecord.Map"/>, ceiling from <see cref="MapDefinition.MaxSlots"/> (or the shared default).
/// These Status tests need no git or docker, but the service is built with the full dependency set.</summary>
public class MapPoolServiceTests
{
    static (MapPoolService pools, InstanceStore instances) Build(TempStore s, int? maxSlots = 4)
    {
        var registry = new RepoRegistryStore(s.Paths);
        var dir = Path.Combine(s.Root, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), """{ "schema":1, "name":"app" }""");
        registry.Add(dir);

        var instances = new InstanceStore(s.Paths);
        var maps = new MapStore(s.Paths, registry);
        maps.Save(new MapDefinition { Name = "app", Repos = [MapRepo.Local("app")], MaxSlots = maxSlots });

        var git = new GitService(new ProcessRunner());
        var workspaces = new WorkspaceService(git, new FilePortStore(s.Paths), instances,
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths,
            new SetupRunner(new ProcessRunner()));
        var resolver = new MapResolver(registry, maps, git, s.Paths);
        return (new MapPoolService(maps, instances, resolver, workspaces, s.Paths), instances);
    }

    static InstanceRecord Workspace(string name, int index, bool claimed, string? label = null) => new()
    {
        Workspace = name,
        Map = "app",
        WorkspaceIndex = index,
        Claimed = claimed,
        Label = label,
    };

    [Fact]
    public void Status_of_a_fresh_map_is_empty_with_the_ceiling()
    {
        using var s = new TempStore();
        var (pools, _) = Build(s, maxSlots: 4);

        var status = pools.Status("app");

        Assert.Equal("app", status.Map);
        Assert.Equal(4, status.MaxSlots);
        Assert.Empty(status.Workspaces);
        Assert.Equal(0, status.ClaimedCount);
        Assert.Equal(4, status.Headroom);
        Assert.False(status.IsExhausted);
    }

    [Fact]
    public void A_map_with_no_maxSlots_falls_back_to_the_default_ceiling()
    {
        using var s = new TempStore();
        var (pools, _) = Build(s, maxSlots: null);

        var status = pools.Status("app");

        Assert.Equal(MapPoolService.DefaultMaxSlots, status.MaxSlots);
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
        Assert.False(status.IsExhausted);
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
    public void ClaimedWorkspaces_scopes_to_the_map()
    {
        using var s = new TempStore();
        var (pools, instances) = Build(s);
        instances.Save(Workspace("app-1", 1, claimed: true));
        instances.Save(Workspace("app-2", 2, claimed: false));

        Assert.Equal(["app-1"], pools.ClaimedWorkspaces("app").Select(w => w.Workspace));
    }

    [Fact]
    public void Status_of_an_unknown_map_throws()
    {
        using var s = new TempStore();
        var (pools, _) = Build(s);
        Assert.Throws<MapException>(() => pools.Status("ghost"));
    }
}
