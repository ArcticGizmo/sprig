using Sprig.App.ViewModels;
using Sprig.Engine.Planning;

namespace Sprig.Tests.App;

public sealed class WorkspaceGraphGroupingTests
{
    static GraphNode N(string key, TopologyKind kind, string? role = null)
        => new(new TopologyNode(key, key.Split('/')[0], key.Split('/')[1], kind, "", role), null, null);

    static readonly GraphNode[] Nodes =
    [
        N("web/web", TopologyKind.App, NodeRoles.Web),
        N("svc/catalog-api", TopologyKind.App, NodeRoles.Api),
        N("svc/orders-api", TopologyKind.App, NodeRoles.Api),
        N("svc/migrate", TopologyKind.Job),
        N("svc/tool", TopologyKind.App),
        N("svc/catalog-db", TopologyKind.Container, NodeRoles.Database),
        N("svc/redis", TopologyKind.Container, NodeRoles.Cache),
        N("svc/sidecar", TopologyKind.ComposeService),
        N("outside/shared-url", TopologyKind.Outside),
    ];

    static readonly TopologyLink[] Links =
    [
        new("web/web", "svc/catalog-api", ["catalog-url"], false),
        new("svc/catalog-api", "svc/catalog-db", ["Db"], false),
        new("svc/catalog-api", "svc/redis", ["Redis"], false),
        new("svc/orders-api", "svc/redis", ["Redis"], false),
        new("svc/orders-api", "outside/shared-url", ["shared-url"], false),
    ];

    static readonly PlannedGroup[] StartGroups =
    [
        new("shop", ["web/web", "svc/catalog-api"]),
        new("orders", ["svc/orders-api", "svc/catalog-api"]),
    ];

    [Fact]
    public void By_repo_each_repo_has_a_box_and_outside_values_none()
    {
        var groups = WorkspaceGraph.GroupsOf(Nodes, Links, GraphGrouping.Repo, StartGroups);

        Assert.Equal("svc", groups["svc/redis"]);
        Assert.Equal("web", groups["web/web"]);
        Assert.Equal(WorkspaceGraph.Loose, groups["outside/shared-url"]);
    }

    [Fact]
    public void By_start_group_an_app_goes_in_the_first_group_naming_it_and_infra_with_its_one_group()
    {
        var groups = WorkspaceGraph.GroupsOf(Nodes, Links, GraphGrouping.StartGroup, StartGroups);

        Assert.Equal("shop", groups["svc/catalog-api"]);
        Assert.Equal("orders", groups["svc/orders-api"]);
        Assert.Equal(WorkspaceGraph.NoStartGroup, groups["svc/tool"]);
        // Only catalog-api, in shop, uses the database; both groups use redis, so it's left loose.
        Assert.Equal("shop", groups["svc/catalog-db"]);
        Assert.Equal(WorkspaceGraph.Loose, groups["svc/redis"]);
        Assert.Equal("orders", groups["outside/shared-url"]);
        Assert.Equal(WorkspaceGraph.Loose, groups["svc/sidecar"]);
    }

    [Fact]
    public void By_start_group_with_no_groups_named_falls_back_to_repo()
    {
        var groups = WorkspaceGraph.GroupsOf(Nodes, Links, GraphGrouping.StartGroup, []);

        Assert.Equal("svc", groups["svc/catalog-api"]);
    }

    [Fact]
    public void By_role_each_kind_of_thing_has_a_box()
    {
        var groups = WorkspaceGraph.GroupsOf(Nodes, Links, GraphGrouping.Role, StartGroups);

        Assert.Equal("APIs", groups["svc/catalog-api"]);
        Assert.Equal("APIs", groups["svc/orders-api"]);
        Assert.Equal("web", groups["web/web"]);
        Assert.Equal(WorkspaceGraph.Jobs, groups["svc/migrate"]);
        Assert.Equal(WorkspaceGraph.OtherApps, groups["svc/tool"]);
        Assert.Equal("databases", groups["svc/catalog-db"]);
        Assert.Equal(WorkspaceGraph.OtherInfra, groups["svc/sidecar"]);
        Assert.Equal(WorkspaceGraph.Loose, groups["outside/shared-url"]);
    }

    [Fact]
    public void A_grouping_gives_the_layout_its_boxes()
    {
        var graph = new WorkspaceGraph(Nodes, Links, null, layered: true, grouping: GraphGrouping.Role, startGroups: StartGroups);

        Assert.Contains("APIs", graph.Layout.Boxes().Select(b => b.Group));
        Assert.DoesNotContain(WorkspaceGraph.Loose, graph.Layout.Boxes().Select(b => b.Group));
    }
}
