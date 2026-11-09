using Sprig.App.ViewModels;
using Sprig.Engine.Planning;

namespace Sprig.Tests.App;

public sealed class WorkspaceGraphSearchTests
{
    static WorkspaceGraph Graph()
    {
        GraphNode N(string key, TopologyKind kind, string detail = "") => new(new TopologyNode(key, key.Split('/')[0], key.Split('/')[1], kind, detail), null, null);
        GraphNode[] nodes =
        [
            N("web/web", TopologyKind.App, ":5170"),
            N("svc/catalog-api", TopologyKind.App, ":8000"),
            N("svc/orders-api", TopologyKind.App, ":8001"),
            N("svc/redis", TopologyKind.Container, "redis:7"),
        ];
        TopologyLink[] links =
        [
            new("web/web", "svc/catalog-api", ["catalog-url"], false),
            new("svc/catalog-api", "svc/redis", ["Redis__Url"], false),
        ];
        return new WorkspaceGraph(nodes, links, null, layered: true);
    }

    [Fact]
    public void A_search_finds_a_node_by_name_repo_or_port_and_whatever_uses_infra_by_that_name()
    {
        var graph = Graph();

        graph.SetSearch("redis");
        Assert.Equal(["svc/catalog-api", "svc/redis"], graph.Matches.Order());

        graph.SetSearch("SVC");
        Assert.Equal(3, graph.Matches.Count);

        graph.SetSearch(":5170");
        Assert.Equal(["web/web"], graph.Matches);

        graph.SetSearch("  ");
        Assert.False(graph.Searching);
        Assert.Empty(graph.Matches);
    }

    [Fact]
    public void What_a_search_finds_is_in_reading_order_top_row_first()
    {
        var graph = Graph();

        graph.SetSearch("-api");

        // catalog-api uses redis and web uses catalog-api, so in rows web is above catalog-api, and orders-api, linked
        // to nothing, sits with the top of its repo.
        Assert.Equal(graph.Matches.Select(k => graph.Layout.Find(k)!).OrderBy(b => b.Y).ThenBy(b => b.X).Select(b => b.Key), graph.Matches);
    }

    [Fact]
    public void Revealing_a_node_picks_it()
    {
        var graph = Graph();
        string? centred = null;
        graph.CenterRequested += k => centred = k;

        graph.Reveal("svc/redis");

        Assert.Equal("svc/redis", graph.Selected);
        Assert.Equal("svc/redis", centred);
    }
}
