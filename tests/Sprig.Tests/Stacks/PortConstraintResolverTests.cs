using Sprig.Core.Config;
using Sprig.Core.Stacks;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Stacks;

/// <summary>Covers tracing a repo input's <c>allowedPorts</c> through its binding to a stack port.</summary>
public class PortConstraintResolverTests
{
    static ResolvedRepo Repo(string name, params InputDeclaration[] inputs)
        => new(name, $@"C:\repos\{name}", new SprigRepoConfig { Name = name, Inputs = inputs });

    static Dictionary<string, IReadOnlyDictionary<string, string>> Binding(
        string repo, params (string input, string expr)[] entries)
        => new()
        {
            [repo] = entries.ToDictionary(e => e.input, e => e.expr, StringComparer.Ordinal),
        };

    [Fact]
    public void Maps_a_single_port_reference_to_its_stack_port()
    {
        var repos = new[] { Repo("web", new InputDeclaration { Name = "frontend", AllowedPorts = "8100-8103" }) };
        var bindings = Binding("web", ("frontend", "${sprig.ports.frontend_port}"));

        var r = PortConstraintResolver.Resolve(repos, bindings, ["frontend_port", "api_port"]);

        Assert.Equal(new HashSet<int> { 8100, 8101, 8102, 8103 }, r["frontend_port"]);
        Assert.False(r.ContainsKey("api_port"));
    }

    [Fact]
    public void Extracts_the_port_from_a_url_shaped_binding()
    {
        var repos = new[] { Repo("web", new InputDeclaration { Name = "cb", AllowedPorts = "8100,8101" }) };
        var bindings = Binding("web", ("cb", "http://localhost:${sprig.ports.web_port}/callback"));

        var r = PortConstraintResolver.Resolve(repos, bindings, ["web_port"]);

        Assert.Equal(new HashSet<int> { 8100, 8101 }, r["web_port"]);
    }

    [Fact]
    public void Unrestricted_inputs_produce_no_constraints()
    {
        var repos = new[] { Repo("web", new InputDeclaration { Name = "frontend" }) };
        var bindings = Binding("web", ("frontend", "${sprig.ports.frontend_port}"));

        var r = PortConstraintResolver.Resolve(repos, bindings, ["frontend_port"]);

        Assert.Empty(r);
    }

    [Fact]
    public void A_literal_binding_cannot_carry_a_restriction()
    {
        var repos = new[] { Repo("web", new InputDeclaration { Name = "frontend", AllowedPorts = "8100-8103" }) };
        var bindings = Binding("web", ("frontend", "3000"));

        var ex = Assert.Throws<PortConstraintException>(
            () => PortConstraintResolver.Resolve(repos, bindings, ["frontend_port"]));
        Assert.Contains("doesn't reference", ex.Message);
    }

    [Fact]
    public void A_multi_port_binding_is_ambiguous()
    {
        var repos = new[] { Repo("web", new InputDeclaration { Name = "pair", AllowedPorts = "8100-8103" }) };
        var bindings = Binding("web", ("pair", "${sprig.ports.a}:${sprig.ports.b}"));

        var ex = Assert.Throws<PortConstraintException>(
            () => PortConstraintResolver.Resolve(repos, bindings, ["a", "b"]));
        Assert.Contains("multiple ports", ex.Message);
    }

    [Fact]
    public void An_unknown_stack_port_is_rejected()
    {
        var repos = new[] { Repo("web", new InputDeclaration { Name = "frontend", AllowedPorts = "8100-8103" }) };
        var bindings = Binding("web", ("frontend", "${sprig.ports.ghost}"));

        var ex = Assert.Throws<PortConstraintException>(
            () => PortConstraintResolver.Resolve(repos, bindings, ["frontend_port"]));
        Assert.Contains("doesn't declare", ex.Message);
    }

    [Fact]
    public void An_invalid_spec_is_rejected()
    {
        var repos = new[] { Repo("web", new InputDeclaration { Name = "frontend", AllowedPorts = "nonsense" }) };
        var bindings = Binding("web", ("frontend", "${sprig.ports.frontend_port}"));

        var ex = Assert.Throws<PortConstraintException>(
            () => PortConstraintResolver.Resolve(repos, bindings, ["frontend_port"]));
        Assert.Contains("invalid allowedPorts", ex.Message);
    }

    [Fact]
    public void Two_inputs_on_one_port_intersect()
    {
        var repos = new[]
        {
            Repo("web",
                new InputDeclaration { Name = "a", AllowedPorts = "8100-8103" },
                new InputDeclaration { Name = "b", AllowedPorts = "8102-8110" }),
        };
        var bindings = Binding("web",
            ("a", "${sprig.ports.p}"),
            ("b", "${sprig.ports.p}"));

        var r = PortConstraintResolver.Resolve(repos, bindings, ["p"]);

        Assert.Equal(new HashSet<int> { 8102, 8103 }, r["p"]);
    }

    [Fact]
    public void Two_inputs_on_one_port_with_no_overlap_conflict()
    {
        var repos = new[]
        {
            Repo("web",
                new InputDeclaration { Name = "a", AllowedPorts = "8100-8101" },
                new InputDeclaration { Name = "b", AllowedPorts = "8200" }),
        };
        var bindings = Binding("web",
            ("a", "${sprig.ports.p}"),
            ("b", "${sprig.ports.p}"));

        var ex = Assert.Throws<PortConstraintException>(
            () => PortConstraintResolver.Resolve(repos, bindings, ["p"]));
        Assert.Contains("no ports in common", ex.Message);
    }

    [Fact]
    public void An_unbound_restricted_input_is_left_for_wiring_to_report()
    {
        // No binding for the input at all — resolver stays silent (StackWiring hard-fails on it).
        var repos = new[] { Repo("web", new InputDeclaration { Name = "frontend", AllowedPorts = "8100-8103" }) };
        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>();

        var r = PortConstraintResolver.Resolve(repos, bindings, ["frontend_port"]);

        Assert.Empty(r);
    }
}
