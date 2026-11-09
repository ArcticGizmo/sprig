using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

public sealed class ForceLayoutTests
{
    static readonly (string Key, string Group, double Radius)[] Nodes =
    [
        ("svc/catalog", "svc", 40), ("svc/orders", "svc", 40), ("svc/pg", "svc", 36), ("svc/migrate", "svc", 38),
        ("web/web", "web", 36), ("web/admin", "web", 38), ("orch/mail", "orch", 36), ("outside/payments", "outside", 50),
    ];

    static readonly (string, string)[] Links =
    [
        ("svc/catalog", "svc/pg"), ("svc/orders", "svc/pg"), ("svc/migrate", "svc/pg"), ("svc/catalog", "svc/migrate"),
        ("web/web", "svc/catalog"), ("web/admin", "svc/orders"), ("svc/orders", "orch/mail"), ("svc/orders", "outside/payments"),
    ];

    static ForceLayout Settled(IReadOnlyDictionary<string, (double, double)>? pinned = null)
    {
        var layout = new ForceLayout(Nodes, Links, pinned);
        layout.Settle();
        return layout;
    }

    [Fact]
    public void The_same_workspace_lays_out_the_same_way_every_time()
    {
        var a = Settled().Bodies.Select(b => (b.Key, Math.Round(b.X, 3), Math.Round(b.Y, 3)));
        var b = Settled().Bodies.Select(b => (b.Key, Math.Round(b.X, 3), Math.Round(b.Y, 3)));

        Assert.Equal(a, b);
    }

    [Fact]
    public void It_settles_with_no_node_over_another()
    {
        var layout = Settled();

        Assert.True(layout.Settled);
        foreach (var a in layout.Bodies)
            foreach (var b in layout.Bodies.Where(b => string.CompareOrdinal(a.Key, b.Key) < 0))
                Assert.True(Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)) >= a.Radius + b.Radius - 1, $"{a.Key} overlaps {b.Key}");
    }

    [Fact]
    public void A_pinned_node_stays_where_it_was_put_and_the_rest_move_round_it()
    {
        var layout = Settled(new Dictionary<string, (double, double)> { ["svc/pg"] = (400, -300) });

        var pg = layout.Find("svc/pg")!;
        Assert.Equal((400.0, -300.0), (pg.X, pg.Y));
        // Its users are drawn toward it rather than left where they'd be without it.
        var free = Settled().Find("svc/catalog")!;
        var drawn = layout.Find("svc/catalog")!;
        Assert.True(Distance(drawn, pg) < Distance(free, pg));
    }

    [Fact]
    public void A_repo_s_nodes_sit_closer_to_each_other_than_to_other_repos()
    {
        var layout = Settled();
        var inGroup = Mean(layout.Bodies.Where(b => b.Group == "svc").ToArray());
        var across = layout.Bodies.Where(b => b.Group == "svc")
            .SelectMany(a => layout.Bodies.Where(b => b.Group == "web").Select(b => Distance(a, b))).Average();

        Assert.True(inGroup < across, $"within svc {inGroup:0}, svc to web {across:0}");
    }

    static double Mean(ForceLayout.Body[] bodies)
        => bodies.SelectMany((a, i) => bodies.Skip(i + 1).Select(b => Distance(a, b))).Average();

    static double Distance(ForceLayout.Body a, ForceLayout.Body b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
