using Sprig.Core.Config;
using Sprig.Core.Maps;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Maps;

/// <summary>E1 (M9 groundwork) — the pure map-graph projection: repo/module nodes, derived provides→needs
/// edges, and each need classified resolved / ambiguous / gap. Same wiring rules as CapabilityResolver, so
/// the canvas shows what a checkout would actually do.</summary>
public class MapGraphProjectionTests
{
    static ResolvedRepo Repo(string name, string json) =>
        new(name, $"/repos/{name}", SprigConfigLoader.Parse(json));

    // A single provider capability with a port output.
    static string Provider(string name, string cap) => $$"""
        { "schema": 3, "name": "{{name}}",
          "modules": [ { "name": "main", "provides": [
            { "capability": "{{cap}}", "outputs": { "port": { "port": true } } } ] } ] }
        """;

    static string Consumer(string name, string cap) => $$"""
        { "schema": 3, "name": "{{name}}",
          "modules": [ { "name": "main", "needs": [ { "capability": "{{cap}}" } ] } ] }
        """;

    [Fact]
    public void A_lone_need_with_one_provider_resolves_to_it_with_an_edge()
    {
        var graph = MapGraphProjection.Project(null,
            [Repo("api", Provider("api", "db")), Repo("web", Consumer("web", "db"))]);

        var need = graph.Nodes.Single(n => n.Repo == "web").Modules.Single().Needs.Single();
        Assert.Equal(WireStatus.Resolved, need.Status);
        Assert.Equal("api", need.ProviderRepo);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(("web", "db", "api"), (edge.FromRepo, edge.Need, edge.ToRepo));
        Assert.True(graph.IsComplete);
        Assert.Empty(graph.Gaps);
        Assert.Empty(graph.Ambiguities);
    }

    [Fact]
    public void A_sibling_module_wins_over_another_repo_nearest_wins()
    {
        // A monorepo whose own module provides 'db', plus another repo that also provides it: the local
        // sibling wins, so it is NOT ambiguous.
        var mono = Repo("mono", """
            { "schema": 3, "name": "mono",
              "modules": [
                { "name": "api", "provides": [ { "capability": "db", "outputs": { "port": { "port": true } } } ] },
                { "name": "web", "needs": [ { "capability": "db" } ] } ] }
            """);
        var other = Repo("other", Provider("other", "db"));

        var graph = MapGraphProjection.Project(null, [mono, other]);
        var need = graph.Nodes.Single(n => n.Repo == "mono").Modules.Single(m => m.Module == "web").Needs.Single();
        Assert.Equal(WireStatus.Resolved, need.Status);
        Assert.Equal("mono", need.ProviderRepo);   // the sibling, not 'other'
        Assert.Empty(graph.Ambiguities);
    }

    [Fact]
    public void Two_providers_and_no_wiring_is_ambiguous_with_the_candidates()
    {
        var graph = MapGraphProjection.Project(null,
            [Repo("a", Provider("a", "db")), Repo("b", Provider("b", "db")), Repo("web", Consumer("web", "db"))]);

        var (mod, need) = Assert.Single(graph.Ambiguities);
        Assert.Equal("web", mod.Repo);
        Assert.Equal(["a", "b"], need.Candidates);
        Assert.False(graph.IsComplete);
        Assert.Empty(graph.Edges);   // an ambiguous need draws no edge until it's disambiguated
    }

    [Fact]
    public void A_map_wiring_entry_disambiguates_to_the_chosen_provider()
    {
        var map = new MapDefinition
        {
            Name = "m",
            Repos = [MapRepo.Local("a"), MapRepo.Local("b"), MapRepo.Local("web")],
            Wiring = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                // web's 'db' need is bridged to capability 'db-a', which only 'a' provides.
                ["web"] = new Dictionary<string, string> { ["db"] = "db-a" },
            },
        };
        var graph = MapGraphProjection.Project(map,
            [Repo("a", Provider("a", "db-a")), Repo("b", Provider("b", "db-b")), Repo("web", Consumer("web", "db"))]);

        var need = graph.Nodes.Single(n => n.Repo == "web").Modules.Single().Needs.Single();
        Assert.Equal(WireStatus.Resolved, need.Status);
        Assert.Equal("a", need.ProviderRepo);
        Assert.Empty(graph.Ambiguities);
    }

    [Fact]
    public void A_need_with_no_provider_is_a_gap()
    {
        var graph = MapGraphProjection.Project(null, [Repo("web", Consumer("web", "db"))]);

        var (mod, need) = Assert.Single(graph.Gaps);
        Assert.Equal("web", mod.Repo);
        Assert.Equal("db", need.Capability);
        Assert.False(graph.IsComplete);
    }

    [Fact]
    public void A_map_default_fills_a_gap_without_an_edge()
    {
        var map = new MapDefinition
        {
            Name = "m",
            Repos = [MapRepo.Local("web")],
            Defaults = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
            {
                ["web"] = new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["db"] = new Dictionary<string, string> { ["url"] = "postgres://staging" },
                },
            },
        };
        var graph = MapGraphProjection.Project(map, [Repo("web", Consumer("web", "db"))]);

        var need = graph.Nodes.Single().Modules.Single().Needs.Single();
        Assert.Equal(WireStatus.Resolved, need.Status);
        Assert.True(need.FromDefault);
        Assert.Empty(graph.Edges);   // a default is a supplied value, not a provider edge
        Assert.True(graph.IsComplete);
    }
}
