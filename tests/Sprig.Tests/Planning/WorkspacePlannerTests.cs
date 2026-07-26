using System.Collections.Generic;
using System.Linq;
using Sprig.Core.Config;
using Sprig.Core.Planning;
using Sprig.Core.Stacks;
using Sprig.Core.Substitution;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Planning;

/// <summary>
/// The plan is the object an overlay transforms, so its contract matters more than most: it must be
/// buildable without allocating anything, it must know which ports actually survive, and every value it
/// produces must say which layer produced it.
/// </summary>
public class WorkspacePlannerTests
{
    const string ApiConfig = """
        { "schema":2, "name":"api",
          "inputs":[ { "name":"port", "example":"5000" }, { "name":"dbPort", "example":"5432" } ],
          "env":[ { "file":".env", "set": { "PORT": "${sprig.port}" } } ] }
        """;

    static ResolvedRepo Repo(string json, string root = "C:/code/api")
    {
        var config = SprigConfigLoader.Parse(json);
        return new ResolvedRepo(config.Name, root, config);
    }

    static Dictionary<string, IReadOnlyDictionary<string, string>> Bind(
        params (string Repo, Dictionary<string, string> Inputs)[] entries)
        => entries.ToDictionary(e => e.Repo, e => (IReadOnlyDictionary<string, string>)e.Inputs);

    static ResolvedStack Stack(IReadOnlyList<string> ports,
        Dictionary<string, IReadOnlyDictionary<string, string>> bindings, params ResolvedRepo[] repos)
        => new("web+api", repos, ports, bindings);

    static ResolvedStack ApiStack() => Stack(
        ["api_port", "postgres_port"],
        Bind(("api", new Dictionary<string, string>
        {
            ["port"] = "${sprig.ports.api_port}",
            ["dbPort"] = "${sprig.ports.postgres_port}",
        })),
        Repo(ApiConfig));

    [Fact]
    public void Plan_captures_every_declared_input_and_starts_from_the_repos_own_config()
    {
        var plan = WorkspacePlanner.Plan(ApiStack(), "feature-x");

        var repo = Assert.Single(plan.Repos);
        Assert.Equal("api", repo.Name);
        Assert.Equal("${sprig.ports.api_port}", repo.Bindings["port"]);
        Assert.Equal("${sprig.ports.postgres_port}", repo.Bindings["dbPort"]);
        // Nothing has overridden anything yet, so the effective config *is* the on-disk config.
        Assert.Same(repo.Source.Config, repo.EffectiveConfig);
        Assert.Empty(plan.Notes);
    }

    [Fact]
    public void Plan_hard_fails_on_an_unbound_input_and_names_it()
    {
        var stack = Stack(["api_port"],
            Bind(("api", new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" })),
            Repo(ApiConfig));

        var ex = Assert.Throws<StackWiringException>(() => WorkspacePlanner.Plan(stack, "feature-x"));
        Assert.Contains("dbPort", ex.Message);
        Assert.Contains("5432", ex.Message);   // the example, so the author knows what to supply
    }

    [Fact]
    public void Referenced_ports_are_the_ones_a_binding_actually_points_at()
    {
        // `unused_port` is declared by the stack but nothing binds to it.
        var stack = Stack(["api_port", "unused_port"],
            Bind(("api", new Dictionary<string, string>
            {
                ["port"] = "${sprig.ports.api_port}",
                ["dbPort"] = "5432",            // a literal — references no port at all
            })),
            Repo(ApiConfig));

        var plan = WorkspacePlanner.Plan(stack, "feature-x");

        Assert.Equal(["api_port"], plan.ReferencedPorts);
        Assert.Equal(["unused_port"], plan.UnreferencedPorts);
    }

    [Fact]
    public void Referenced_ports_keep_declaration_order_and_survive_a_with_expression()
    {
        var plan = WorkspacePlanner.Plan(ApiStack(), "feature-x");
        Assert.Equal(["api_port", "postgres_port"], plan.ReferencedPorts);

        // An overlay rewrites dbPort to a fixed shared port; postgres_port is now referenced by nothing.
        // ReferencedPorts must recompute — a cached value would keep reserving the freed port.
        var repo = plan.Repos[0];
        var rewired = plan with
        {
            Repos = [repo with
            {
                Bindings = new Dictionary<string, string>(repo.Bindings) { ["dbPort"] = "5432" },
            }],
        };

        Assert.Equal(["api_port"], rewired.ReferencedPorts);
        Assert.Equal(["postgres_port"], rewired.UnreferencedPorts);
    }

    [Fact]
    public void Bind_resolves_expressions_and_gives_each_repo_a_usable_scope()
    {
        var plan = WorkspacePlanner.Plan(ApiStack(), "feature-x");

        var bound = WorkspacePlanner.Bind(plan, new Dictionary<string, int>
        {
            ["api_port"] = 8021,
            ["postgres_port"] = 8034,
        });

        var repo = Assert.Single(bound.Repos);
        Assert.Equal("8021", repo.Inputs["port"]);
        Assert.Equal("8034", repo.Inputs["dbPort"]);
        // The scope is what env/compose templates resolve against.
        Assert.Equal("8021", SubstitutionEngine.Resolve("${sprig.port}", repo.Scope));
        Assert.Equal("feature-x", SubstitutionEngine.Resolve("${sprig.workspace}", repo.Scope));
    }

    [Fact]
    public void Bind_notes_say_which_layer_produced_each_value()
    {
        var plan = WorkspacePlanner.Plan(ApiStack(), "feature-x");
        var bound = WorkspacePlanner.Bind(plan, new Dictionary<string, int>
        {
            ["api_port"] = 8021,
            ["postgres_port"] = 8034,
        });

        var portNote = Assert.Single(bound.Notes, n => n.Target == PlanTargets.Port("api_port"));
        Assert.Equal(PlanLayer.Stack, portNote.Layer);
        Assert.Equal("8021", portNote.Value);
        Assert.Null(portNote.Repo);

        var inputNote = Assert.Single(bound.NotesFor("api"), n => n.Target == PlanTargets.Input("port"));
        Assert.Equal(PlanLayer.Stack, inputNote.Layer);
        Assert.Equal("8021", inputNote.Value);
        Assert.Equal("${sprig.ports.api_port}", inputNote.Expression);
        Assert.Null(inputNote.Replaced);
        Assert.False(bound.HasOverrides);
    }

    [Fact]
    public void Preview_renders_placeholders_rather_than_inventing_port_numbers()
    {
        var plan = WorkspacePlanner.Plan(ApiStack(), "feature-x");

        var preview = WorkspacePlanner.Preview(plan);

        Assert.Empty(preview.Ports);
        Assert.Equal("{api_port}", preview.Repos[0].Inputs["port"]);
        Assert.Equal("{postgres_port}", preview.Repos[0].Inputs["dbPort"]);
    }

    // M1's overlay engine records its decisions as plan notes. Bind has to honour them, so the contract
    // is pinned here before there is an engine to produce them.
    [Fact]
    public void Bind_honours_an_override_note_recorded_at_plan_time()
    {
        var plan = WorkspacePlanner.Plan(ApiStack(), "feature-x");
        var repo = plan.Repos[0];

        var overlaid = plan with
        {
            Repos = [repo with
            {
                Bindings = new Dictionary<string, string>(repo.Bindings) { ["dbPort"] = "5432" },
            }],
            Notes =
            [
                new PlanNote(PlanLayer.Shared, PlanTargets.Input("dbPort"), "5432")
                {
                    Repo = "api",
                    Replaced = "${sprig.ports.postgres_port}",
                    Source = "postgres-16",
                },
            ],
        };

        var bound = WorkspacePlanner.Bind(overlaid, new Dictionary<string, int> { ["api_port"] = 8021 });

        var note = Assert.Single(bound.NotesFor("api"), n => n.Target == PlanTargets.Input("dbPort"));
        Assert.Equal(PlanLayer.Shared, note.Layer);
        Assert.Equal("5432", note.Value);
        Assert.Equal("postgres-16", note.Source);
        // postgres_port was never allocated, so the displaced value genuinely has no number. Showing the
        // expression is honest; showing a made-up port would not be.
        Assert.Equal("${sprig.ports.postgres_port}", note.Replaced);
        Assert.True(bound.HasOverrides);
    }

    [Fact]
    public void A_repo_with_no_inputs_plans_and_binds_cleanly()
    {
        var stack = new ResolvedStack(null,
            [Repo("""{ "schema":2, "name":"solo" }""")],
            [], new Dictionary<string, IReadOnlyDictionary<string, string>>());

        var bound = WorkspacePlanner.Bind(WorkspacePlanner.Plan(stack, "solo-ws"), new Dictionary<string, int>());

        Assert.Empty(Assert.Single(bound.Repos).Inputs);
        Assert.Empty(bound.Notes);
    }
}
