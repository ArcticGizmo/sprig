using Sprig.App.ViewModels;
using Sprig.Engine.Planning;

namespace Sprig.Tests.App;

public sealed class LayeredLayoutTests
{
    /// <summary>A web app through a gateway to two services sharing a database, a job they wait for, a docs site
    /// linked to nothing, and an outside value.</summary>
    static readonly (string Key, string Group, double Radius, bool Bottom)[] Nodes =
    [
        ("web/web", "web", 40, false), ("web/storybook", "web", 40, false),
        ("gw/gateway", "gw", 40, false),
        ("svc/catalog", "svc", 40, false), ("svc/orders", "svc", 40, false), ("svc/migrate", "svc", 40, false), ("svc/pg", "svc", 36, true),
        ("docs/docs", "docs", 36, false),
        ("outside/payments", "outside", 50, true),
    ];

    static readonly (string, string)[] Links =
    [
        ("web/web", "gw/gateway"), ("gw/gateway", "svc/catalog"), ("gw/gateway", "svc/orders"),
        ("svc/catalog", "svc/pg"), ("svc/orders", "svc/pg"), ("svc/migrate", "svc/pg"),
        ("svc/catalog", "svc/migrate"), ("svc/orders", "svc/migrate"), ("svc/orders", "outside/payments"),
    ];

    static LayeredLayout Layout(IEnumerable<(string, string)>? links = null) => new(Nodes, links ?? Links, "outside");

    static double Y(LayeredLayout l, string key) => l.Find(key)!.Y;

    [Fact]
    public void What_uses_something_sits_above_it_with_infra_and_outside_values_along_the_bottom()
    {
        var l = Layout();

        Assert.True(Y(l, "web/web") < Y(l, "gw/gateway"));
        Assert.True(Y(l, "gw/gateway") < Y(l, "svc/catalog"));
        Assert.Equal(Y(l, "svc/catalog"), Y(l, "svc/orders"));
        Assert.True(Y(l, "svc/orders") < Y(l, "svc/migrate"));
        Assert.True(Y(l, "svc/migrate") < Y(l, "svc/pg"));
        Assert.Equal(Y(l, "svc/pg"), Y(l, "outside/payments"));
        Assert.DoesNotContain(Links, e => l.RunsBack(e.Item1, e.Item2));
    }

    [Fact]
    public void A_node_linked_to_nothing_sits_with_the_top_of_its_group()
    {
        var l = Layout();

        Assert.Equal(Y(l, "web/web"), Y(l, "web/storybook"));
    }

    [Fact]
    public void Services_that_use_each_other_lay_out_with_as_few_links_running_back_as_the_loop_needs()
    {
        // catalog and orders call each other, and the gateway calls both: one of the pair has to run back up.
        var l = Layout([.. Links, ("svc/catalog", "svc/orders"), ("svc/orders", "svc/catalog")]);

        var back = new[] { ("svc/catalog", "svc/orders"), ("svc/orders", "svc/catalog") }.Count(e => l.RunsBack(e.Item1, e.Item2));
        Assert.Equal(1, back);
        Assert.True(Y(l, "gw/gateway") < Math.Min(Y(l, "svc/catalog"), Y(l, "svc/orders")));
    }

    [Fact]
    public void No_two_nodes_overlap_and_each_group_keeps_a_column_of_its_own()
    {
        var l = Layout();

        foreach (var a in l.Bodies)
            foreach (var b in l.Bodies.Where(b => string.CompareOrdinal(a.Key, b.Key) < 0 && Math.Abs(a.Y - b.Y) < 1))
                Assert.True(Math.Abs(a.X - b.X) >= a.Radius + b.Radius, $"{a.Key} overlaps {b.Key}");
        var boxes = l.Boxes().ToArray();
        foreach (var a in boxes)
            foreach (var b in boxes.Where(b => string.CompareOrdinal(a.Group, b.Group) < 0))
                Assert.False(a.Box.Overlaps(b.Box, 0), $"{a.Group} and {b.Group}");
    }

    [Fact]
    public void A_group_sits_beside_the_one_it_has_most_links_with()
    {
        var l = Layout();
        var columns = l.Boxes().OrderBy(b => b.Box.Left).Select(b => b.Group).ToList();

        Assert.Equal(1, Math.Abs(columns.IndexOf("web") - columns.IndexOf("gw")));
        Assert.Equal(1, Math.Abs(columns.IndexOf("gw") - columns.IndexOf("svc")));
    }

    [Fact]
    public void A_link_that_passes_a_row_bends_round_the_nodes_in_it()
    {
        // web uses migrate directly, two rows down, past the gateway's row and the services'.
        var l = Layout([.. Links, ("web/web", "svc/migrate")]);

        var route = l.Route("web/web", "svc/migrate");
        Assert.NotEmpty(route);
        foreach (var (x, y) in route)
            foreach (var b in l.Bodies.Where(b => Math.Abs(b.Y - y) < 1))
                Assert.True(Math.Abs(b.X - x) >= b.Radius, $"bends through {b.Key}");
    }

    [Fact]
    public void The_same_workspace_lays_out_the_same_way_every_time()
    {
        Assert.Equal(Layout().Bodies.Select(b => (b.Key, b.X, b.Y)), Layout().Bodies.Select(b => (b.Key, b.X, b.Y)));
    }

    [Fact]
    public void Infra_and_outside_values_a_node_uses_are_its_tags_and_their_links_are_left_out()
    {
        TopologyNode N(string key, TopologyKind kind) => new(key, key.Split('/')[0], key.Split('/')[1], kind, "");
        var nodes = new[]
        {
            new GraphNode(N("shop/orders-api", TopologyKind.App), null, null),
            new GraphNode(N("shop/web", TopologyKind.App), null, null),
            new GraphNode(N("shop/shop-db", TopologyKind.ComposeService), null, null),
            new GraphNode(N("outside/identity-url", TopologyKind.Outside), null, null),
        };
        TopologyLink[] links =
        [
            new("shop/orders-api", "shop/shop-db", ["ConnectionStrings__Db"], false),
            new("shop/orders-api", "outside/identity-url", ["identity-url"], false),
            new("shop/web", "shop/orders-api", ["api-url"], false),
        ];

        var graph = new WorkspaceGraph(nodes, links, null, layered: true);

        var api = graph.Find("shop/orders-api")!;
        Assert.Equal(["db", "identity-url"], api.Tags.Select(t => t.TagName));
        Assert.True(graph.IsTagLink(links[0]));
        Assert.True(graph.IsTagLink(links[1]));
        Assert.False(graph.IsTagLink(links[2]));
        Assert.Empty(graph.Find("shop/shop-db")!.Tags);
    }
}
