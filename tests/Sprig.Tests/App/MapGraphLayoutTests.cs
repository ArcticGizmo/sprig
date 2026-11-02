using Avalonia;
using Sprig.App.Maps;
using Sprig.App.Rendering;
using Sprig.Core.Maps;

namespace Sprig.Tests.App;

/// <summary>The layered layout behind the Maps wiring canvas.</summary>
public class MapGraphLayoutTests
{
    static readonly MapGraphLayout Layout = MapGraphLayout.Compute(WiringExample.Inspect());

    static GraphNode Node(string id) => Layout.Nodes.Single(n => n.Id == id);

    [Fact]
    public void Every_module_and_every_unmet_or_constant_need_gets_a_node()
    {
        var inspection = WiringExample.Inspect();
        Assert.Equal(inspection.Modules.Count(), Layout.Nodes.Count(n => n.Kind == GraphNodeKind.Module));
        Assert.Equal(inspection.Gaps.Select(MapGraphLayout.GapId).Order(), Layout.Nodes.Where(n => n.Kind == GraphNodeKind.Gap).Select(n => n.Id).Order());
        Assert.Equal(inspection.Defaulted.Select(MapGraphLayout.DefaultId), Layout.Nodes.Where(n => n.Kind == GraphNodeKind.Default).Select(n => n.Id));
    }

    [Fact]
    public void A_module_sits_above_everything_it_relies_on()
    {
        foreach (var edge in Layout.Edges.Where(e => e.Kind is GraphEdgeKind.Wired or GraphEdgeKind.Default or GraphEdgeKind.Gap))
            Assert.True(Node(edge.From).Bounds.Bottom <= Node(edge.To).Bounds.Top + 0.5,
                $"{edge.From} {Node(edge.From).Bounds} should sit above {edge.To} {Node(edge.To).Bounds}; clusters {string.Join(", ", Layout.Clusters.Select(c => c.Bounds))}");
    }

    [Fact]
    public void Nodes_do_not_overlap_and_fit_the_drawing()
    {
        var area = new Rect(Layout.Size);
        foreach (var n in Layout.Nodes)
            Assert.True(area.Contains(n.Bounds), $"{n.Id} is outside the drawing");
        foreach (var (a, i) in Layout.Nodes.Select((n, i) => (n, i)))
            foreach (var b in Layout.Nodes.Skip(i + 1))
                Assert.False(a.Bounds.Intersects(b.Bounds), $"{a.Id} overlaps {b.Id}");
    }

    [Fact]
    public void A_repo_with_several_modules_is_a_box_holding_only_its_own_modules()
    {
        var shop = Assert.Single(Layout.Clusters);
        Assert.Equal("shop", shop.Repo);
        foreach (var n in Layout.Nodes)
        {
            var member = n.Module?.Repo == "shop";
            Assert.Equal(member, shop.Bounds.Contains(n.Bounds));
            if (!member) Assert.False(shop.Bounds.Intersects(n.Bounds), $"{n.Id} intrudes on the shop box");
        }
    }

    [Fact]
    public void Edges_carry_their_capabilities_and_end_at_their_target()
    {
        var hub = Layout.Edges.Single(e => e.From == MapGraphLayout.ModuleId("billing", "app") && e.To == MapGraphLayout.ModuleId("auth", "app"));
        Assert.Equal(["auth-api", "auth-realm"], hub.Labels);

        foreach (var e in Layout.Edges)
        {
            Assert.True(e.Path.Count >= 2, $"{e.From} → {e.To} has no route");
            var target = Node(e.To).Bounds.Inflate(2);
            Assert.True(target.Contains(e.ArrowTip), $"{e.From} → {e.To} stops short of its target");
        }
    }

    [Fact]
    public void Two_providers_draw_two_ambiguous_edges_and_a_near_miss_points_at_its_lookalike()
    {
        var shopApi = MapGraphLayout.ModuleId("shop", "api");
        Assert.Equal(2, Layout.Edges.Count(e => e.From == shopApi && e.Kind == GraphEdgeKind.Ambiguous));
        // The wired need to the same module keeps an edge of its own.
        var toCatalog = Layout.Edges.Where(e => e.From == shopApi && e.To == MapGraphLayout.ModuleId("catalog", "app")).ToList();
        Assert.Equal([(GraphEdgeKind.Wired, "catalog-api"), (GraphEdgeKind.Ambiguous, "cache")],
            toCatalog.Select(e => (e.Kind, e.Labels.Single())).OrderBy(x => x.Kind));

        var gap = WiringExample.Inspect().Gaps.Single(n => n.Repo == "admin");
        var similar = Layout.Edges.Single(e => e.Kind == GraphEdgeKind.Similar);
        Assert.Equal((MapGraphLayout.GapId(gap), MapGraphLayout.ModuleId("auth", "app")), (similar.From, similar.To));
    }

    [Fact]
    public void The_layout_is_the_same_every_time()
    {
        var again = MapGraphLayout.Compute(WiringExample.Inspect());
        Assert.Equal(Layout.Nodes.Select(n => (n.Id, n.Bounds)), again.Nodes.Select(n => (n.Id, n.Bounds)));
    }

    [Fact]
    public void Two_modules_that_need_each_other_still_lay_out_and_route()
    {
        var repo = new Sprig.Core.Workspaces.ResolvedRepo("duo", "/example/duo", Sprig.Core.Config.SprigConfigLoader.Parse("""
            { "schema": 1, "name": "duo", "modules": [
              { "name": "api", "provides": [ { "capability": "duo-api", "ports": { "port": true } } ], "needs": [ { "capability": "duo-web" } ] },
              { "name": "web", "provides": [ { "capability": "duo-web", "ports": { "port": true } } ], "needs": [ { "capability": "duo-api" } ] } ] }
            """));
        var layout = MapGraphLayout.Compute(MapInspector.Inspect(new MapDefinition { Name = "m", Repos = [MapRepo.Local("duo")] }, [repo]));

        Assert.Equal(2, layout.Edges.Count);
        Assert.All(layout.Edges, e => Assert.True(e.Path.Count >= 2));
        Assert.False(layout.Nodes[0].Bounds.Intersects(layout.Nodes[1].Bounds));
    }

    [Fact]
    public void An_empty_map_lays_out_to_nothing()
    {
        var empty = MapGraphLayout.Compute(MapInspector.Inspect(new MapDefinition { Name = "m" }, []));
        Assert.Empty(empty.Nodes);
    }
}
