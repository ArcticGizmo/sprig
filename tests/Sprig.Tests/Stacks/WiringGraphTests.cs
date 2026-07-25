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

    // --- Phase 0: transform nodes, the workspace source, and multi-port edges ----------------

    /// <summary>Build a single-repo stack with one input bound to <paramref name="expr"/>.</summary>
    static WiringGraph OneInput(string expr, params string[] ports)
    {
        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["app"] = new Dictionary<string, string> { ["value"] = expr },
        };
        return WiringGraph.Build(["app"], ports,
            new Dictionary<string, IReadOnlyList<string>> { ["app"] = ["value"] }, bindings);
    }

    static WiringPin OnePin(string expr, params string[] ports) =>
        OneInput(expr, ports).Repos.Single().Pins.Single();

    [Fact]
    public void A_transform_binding_gets_a_centre_node_listing_its_ports()
    {
        var g = Build();
        var node = g.TransformNodes.Single(n => n is { Repo: "vue", Input: "apiUrl" });
        Assert.Equal("http://localhost:${sprig.ports.api_port}", node.Expression);
        Assert.Equal(["api_port"], node.Ports);
        Assert.False(node.UsesWorkspace);

        var pin = g.Repos.Single(r => r.Repo == "vue").Pins.Single(p => p.Input == "apiUrl");
        Assert.True(pin.NeedsTransform);
    }

    [Fact]
    public void An_identity_binding_gets_no_transform_node()
    {
        var g = Build();
        Assert.DoesNotContain(g.TransformNodes, n => n is { Repo: "vue", Input: "frontend" });
        var pin = g.Repos.Single(r => r.Repo == "vue").Pins.Single(p => p.Input == "frontend");
        Assert.False(pin.NeedsTransform);
    }

    [Fact]
    public void A_bare_workspace_reference_is_a_source_not_a_transform()
    {
        var g = OneInput("${sprig.workspace}");
        var pin = g.Repos.Single().Pins.Single();

        Assert.True(pin.UsesWorkspace);
        Assert.Empty(pin.Ports);
        Assert.Equal(1, pin.SourceCount);
        Assert.False(pin.NeedsTransform);
        Assert.False(pin.IsLiteral);           // it has a source behind it, so not a typed literal
        Assert.Empty(g.TransformNodes);
        Assert.Equal(1, g.Workspace.ConsumerCount);
        Assert.True(g.Workspace.Used);
    }

    [Fact]
    public void A_wrapped_workspace_reference_gets_a_transform_node()
    {
        var g = OneInput("myapp-${sprig.workspace}");
        var pin = g.Repos.Single().Pins.Single();

        Assert.True(pin.UsesWorkspace);
        Assert.True(pin.NeedsTransform);
        var node = g.TransformNodes.Single();
        Assert.True(node.UsesWorkspace);
        Assert.Empty(node.Ports);
        Assert.Equal(1, g.Workspace.ConsumerCount);
    }

    [Fact]
    public void A_shared_workspace_source_counts_every_consumer()
    {
        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["a"] = new Dictionary<string, string> { ["name"] = "${sprig.workspace}" },
            ["b"] = new Dictionary<string, string> { ["name"] = "svc-${sprig.workspace}" },
        };
        var g = WiringGraph.Build(["a", "b"], [],
            new Dictionary<string, IReadOnlyList<string>> { ["a"] = ["name"], ["b"] = ["name"] }, bindings);

        Assert.Equal(2, g.Workspace.ConsumerCount);
        Assert.True(g.Workspace.Shared);
    }

    [Fact]
    public void A_multi_port_expression_draws_an_edge_per_port_and_one_transform_node()
    {
        var g = OneInput("${sprig.ports.a}:${sprig.ports.b}", "a", "b");

        var edges = g.Edges.Where(e => e is { Repo: "app", Input: "value" }).ToList();
        Assert.Equal(2, edges.Count);
        Assert.Equal(["a", "b"], edges.Select(e => e.Port).OrderBy(p => p));

        var node = g.TransformNodes.Single();
        Assert.Equal(["a", "b"], node.Ports);
        Assert.False(node.UsesWorkspace);

        var pin = g.Repos.Single().Pins.Single();
        Assert.Null(pin.Port);                 // no single clean cable — it fans in from two ports
        Assert.Equal(["a", "b"], pin.Ports);
        Assert.True(pin.NeedsTransform);
    }

    [Fact]
    public void A_pure_literal_has_no_sources_and_no_transform_node()
    {
        var g = OneInput("production");
        var pin = g.Repos.Single().Pins.Single();

        Assert.True(pin.IsLiteral);
        Assert.Equal(0, pin.SourceCount);
        Assert.False(pin.UsesWorkspace);
        Assert.False(pin.NeedsTransform);
        Assert.Empty(g.Edges);
        Assert.Empty(g.TransformNodes);
        Assert.Equal(0, g.Workspace.ConsumerCount);
    }

    [Fact]
    public void A_pin_carries_its_expression_for_inline_display()
    {
        var pin = OnePin("http://localhost:${sprig.ports.a}", "a");
        Assert.Equal("http://localhost:${sprig.ports.a}", pin.Expression);
    }
}
