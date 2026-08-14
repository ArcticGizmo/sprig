using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class RepoGraphTests
{
    static IReadOnlyDictionary<string, IReadOnlyList<string>> Inputs(
        params (string Repo, string[] Names)[] entries) =>
        entries.ToDictionary(e => e.Repo, e => (IReadOnlyList<string>)e.Names);

    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Bindings(
        params (string Repo, (string Input, string Expr)[] Rows)[] entries) =>
        entries.ToDictionary(
            e => e.Repo,
            e => (IReadOnlyDictionary<string, string>)e.Rows.ToDictionary(r => r.Input, r => r.Expr));

    static IReadOnlyDictionary<string, string> Owners(params (string Port, string Repo)[] entries) =>
        entries.ToDictionary(e => e.Port, e => e.Repo);

    [Fact]
    public void Owned_port_with_one_other_consumer_becomes_a_directed_line()
    {
        // api serves on api_port; web reads it. api owns api_port → a clean api→web dependency line.
        var g = RepoGraph.Build(
            repos: ["api", "web"],
            ports: ["api_port"],
            repoInputs: Inputs(("api", ["port"]), ("web", ["apiUrl"])),
            bindings: Bindings(
                ("api", [("port", "${sprig.ports.api_port}")]),
                ("web", [("apiUrl", "http://localhost:${sprig.ports.api_port}")])),
            owners: Owners(("api_port", "api")));

        var edge = Assert.Single(g.Edges);
        Assert.Equal(("api", "web", "api_port"), (edge.Owner, edge.Consumer, edge.Port));
        // No chips anywhere — the relationship is fully carried by the line.
        Assert.All(g.Nodes, n => Assert.Empty(n.Chips));
        Assert.Empty(g.UnownedPorts);
        Assert.Equal(new[] { "api_port" }, g.Nodes.Single(n => n.Repo == "api").Owns);
    }

    [Fact]
    public void Shared_port_becomes_chips_with_a_usage_count_not_lines()
    {
        // bus_port is owned by rmq but read by three services → three chips, each labelled ×3, no line.
        var g = RepoGraph.Build(
            repos: ["rmq", "a", "b", "c"],
            ports: ["bus_port"],
            repoInputs: Inputs(("rmq", ["port"]), ("a", ["bus"]), ("b", ["bus"]), ("c", ["bus"])),
            bindings: Bindings(
                ("rmq", [("port", "${sprig.ports.bus_port}")]),
                ("a", [("bus", "${sprig.ports.bus_port}")]),
                ("b", [("bus", "${sprig.ports.bus_port}")]),
                ("c", [("bus", "${sprig.ports.bus_port}")])),
            owners: Owners(("bus_port", "rmq")));

        Assert.Empty(g.Edges);
        foreach (var repo in new[] { "a", "b", "c" })
        {
            var chip = Assert.Single(g.Nodes.Single(n => n.Repo == repo).Chips);
            Assert.Equal(("bus_port", 3), (chip.Port, chip.UsedBy));
        }
        // The producer shows no chip for its own port; it badges it as owned instead.
        Assert.Empty(g.Nodes.Single(n => n.Repo == "rmq").Chips);
        Assert.Equal(new[] { "bus_port" }, g.Nodes.Single(n => n.Repo == "rmq").Owns);
    }

    [Fact]
    public void Unowned_port_is_drawn_as_chips_and_flagged_for_owner_assignment()
    {
        var g = RepoGraph.Build(
            repos: ["a", "b"],
            ports: ["shared"],
            repoInputs: Inputs(("a", ["x"]), ("b", ["y"])),
            bindings: Bindings(
                ("a", [("x", "${sprig.ports.shared}")]),
                ("b", [("y", "${sprig.ports.shared}")])),
            owners: Owners());

        Assert.Empty(g.Edges);                                   // no owner → can't draw a line
        Assert.Equal(new[] { "shared" }, g.UnownedPorts);        // surfaced so the UI can prompt
        Assert.Equal(2, g.Nodes.Sum(n => n.Chips.Count));
    }

    [Fact]
    public void Assigning_an_owner_promotes_a_two_repo_shared_port_to_a_line()
    {
        // Same two-consumer port as above, but naming one consumer the owner leaves exactly one other
        // consumer — so it flips from chips to a directed line.
        var g = RepoGraph.Build(
            repos: ["a", "b"],
            ports: ["shared"],
            repoInputs: Inputs(("a", ["x"]), ("b", ["y"])),
            bindings: Bindings(
                ("a", [("x", "${sprig.ports.shared}")]),
                ("b", [("y", "${sprig.ports.shared}")])),
            owners: Owners(("shared", "a")));

        var edge = Assert.Single(g.Edges);
        Assert.Equal(("a", "b", "shared"), (edge.Owner, edge.Consumer, edge.Port));
        Assert.All(g.Nodes, n => Assert.Empty(n.Chips));
        Assert.Empty(g.UnownedPorts);
    }

    [Fact]
    public void A_port_only_its_owner_consumes_draws_nothing_but_is_still_owned()
    {
        var g = RepoGraph.Build(
            repos: ["api"],
            ports: ["api_port"],
            repoInputs: Inputs(("api", ["port"])),
            bindings: Bindings(("api", [("port", "${sprig.ports.api_port}")])),
            owners: Owners(("api_port", "api")));

        Assert.Empty(g.Edges);
        Assert.All(g.Nodes, n => Assert.Empty(n.Chips));
        Assert.Empty(g.UnownedPorts);
        Assert.Equal(new[] { "api_port" }, g.Nodes.Single().Owns);
    }

    [Fact]
    public void An_owner_outside_the_stack_is_ignored_and_the_port_falls_back_to_chips()
    {
        var g = RepoGraph.Build(
            repos: ["a", "b"],
            ports: ["shared"],
            repoInputs: Inputs(("a", ["x"]), ("b", ["y"])),
            bindings: Bindings(
                ("a", [("x", "${sprig.ports.shared}")]),
                ("b", [("y", "${sprig.ports.shared}")])),
            owners: Owners(("shared", "ghost")));   // ghost isn't in the stack

        Assert.Empty(g.Edges);
        Assert.Equal(new[] { "shared" }, g.UnownedPorts);
        Assert.Equal(2, g.Nodes.Sum(n => n.Chips.Count));
    }

    [Fact]
    public void Nodes_carry_their_declared_inputs_in_declaration_order()
    {
        var g = RepoGraph.Build(
            repos: ["a"],
            ports: [],
            repoInputs: Inputs(("a", ["first", "second", "third"])),
            bindings: Bindings(("a", [("first", "literal")])),
            owners: Owners());

        Assert.Equal(new[] { "first", "second", "third" }, g.Nodes.Single().Inputs.Select(p => p.Name));
    }

    [Fact]
    public void Pins_report_bound_and_which_one_the_repo_provides()
    {
        // api owns api_port and binds its own 'port' input to it (that pin is what it provides); 'log' is
        // a bare literal (bound but not a provided port); 'spare' is left empty (unbound).
        var g = RepoGraph.Build(
            repos: ["api"],
            ports: ["api_port"],
            repoInputs: Inputs(("api", ["port", "log", "spare"])),
            bindings: Bindings(("api", [("port", "${sprig.ports.api_port}"), ("log", "info")])),
            owners: Owners(("api_port", "api")));

        var pins = g.Nodes.Single().Inputs.ToDictionary(p => p.Name);
        Assert.True(pins["port"].Bound);
        Assert.True(pins["port"].Owned);   // provides api_port → drawn as a star
        Assert.True(pins["log"].Bound);
        Assert.False(pins["log"].Owned);
        Assert.False(pins["spare"].Bound); // empty → grey dot
        Assert.False(pins["spare"].Owned);
    }
}
