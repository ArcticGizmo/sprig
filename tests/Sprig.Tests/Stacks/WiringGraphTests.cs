using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class WiringGraphTests
{
    static readonly IReadOnlyList<string> Repos = ["vue", "api"];
    static readonly IReadOnlyList<string> Ports = ["frontend_port", "api_port", "postgres_port"];

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Inputs =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["vue"] = ["frontend", "apiUrl"],
            ["api"] = ["port", "dbPort"],
        };

    static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Bindings =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string>
            {
                ["frontend"] = "${sprig.ports.frontend_port}",
                ["apiUrl"] = "http://localhost:${sprig.ports.api_port}",
            },
            ["api"] = new Dictionary<string, string>
            {
                ["port"] = "${sprig.ports.api_port}",
                ["dbPort"] = "${sprig.ports.postgres_port}",
            },
        };

    static WiringGraph Build() => WiringGraph.Build(Repos, Ports, Inputs, Bindings);

    [Fact]
    public void Every_repo_and_port_becomes_a_node()
    {
        var g = Build();
        Assert.Equal(["vue", "api"], g.Repos.Select(r => r.Repo));
        Assert.Equal(2, g.Repos.Single(r => r.Repo == "vue").Pins.Count);
        Assert.Equal(3, g.Ports.Count);
    }

    [Fact]
    public void The_shared_port_is_marked_and_counts_its_consumers()
    {
        var g = Build();
        var apiPort = g.Ports.Single(p => p.Name == "api_port");
        Assert.True(apiPort.Shared);
        Assert.Equal(2, apiPort.ConsumerCount);

        var frontend = g.Ports.Single(p => p.Name == "frontend_port");
        Assert.False(frontend.Shared);
        Assert.Equal(1, frontend.ConsumerCount);
    }

    [Fact]
    public void Edges_carry_transform_and_shared_flags()
    {
        var g = Build();

        var transformEdge = g.Edges.Single(e => e is { Repo: "vue", Input: "apiUrl" });
        Assert.Equal("api_port", transformEdge.Port);
        Assert.True(transformEdge.Transform);
        Assert.True(transformEdge.Shared);

        var identityEdge = g.Edges.Single(e => e is { Repo: "vue", Input: "frontend" });
        Assert.False(identityEdge.Transform);
        Assert.False(identityEdge.Shared);
    }

    [Fact]
    public void A_pin_bound_to_one_port_records_it_for_a_clean_cable()
    {
        var g = Build();
        var pin = g.Repos.Single(r => r.Repo == "api").Pins.Single(p => p.Input == "port");
        Assert.True(pin.HasPort);
        Assert.Equal("api_port", pin.Port);
        Assert.True(pin.Shared);
    }

    [Fact]
    public void An_unbound_pin_has_no_port_and_no_edge()
    {
        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string> { ["frontend"] = "${sprig.ports.frontend_port}" },
            // apiUrl left unbound
        };
        var g = WiringGraph.Build(["vue"], ["frontend_port"],
            new Dictionary<string, IReadOnlyList<string>> { ["vue"] = ["frontend", "apiUrl"] }, bindings);

        var apiUrl = g.Repos.Single().Pins.Single(p => p.Input == "apiUrl");
        Assert.Equal(BindingKind.Unbound, apiUrl.Kind);
        Assert.False(apiUrl.HasPort);
        Assert.DoesNotContain(g.Edges, e => e.Input == "apiUrl");
    }

    [Fact]
    public void A_reference_to_an_undeclared_port_is_flagged_and_draws_no_edge()
    {
        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string> { ["frontend"] = "${sprig.ports.ghost}" },
        };
        var g = WiringGraph.Build(["vue"], ["frontend_port"],
            new Dictionary<string, IReadOnlyList<string>> { ["vue"] = ["frontend"] }, bindings);

        var pin = g.Repos.Single().Pins.Single();
        Assert.True(pin.UndeclaredPort);
        Assert.False(pin.HasPort);
        Assert.Empty(g.Edges);
    }
}
