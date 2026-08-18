using Sprig.Core.Config;
using Sprig.Core.Demo;
using Sprig.Core.Maps;
using Sprig.Core.Stacks;   // RepoRegistryStore
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Demo;

/// <summary>
/// Guards the tour's fixture content against schema drift. These are the tests referred to in
/// docs/guided-tour-plan.md §7: the sample repos are authored to the same <c>.sprig.json</c> schema a
/// user's repo uses, so bumping <c>SupportedSchema</c> or tightening a validation rule must fail
/// here — in CI — rather than on a new user's first launch.
/// </summary>
public class SampleFixtureTests
{
    [Fact]
    public void Every_fixture_resource_is_embedded_and_non_empty()
    {
        foreach (var file in SampleFixtures.All)
            Assert.False(string.IsNullOrWhiteSpace(SampleFixtures.Read(file.Resource)),
                $"fixture '{file.Resource}' is missing or empty");
    }

    [Theory]
    [InlineData("Sprig.Demo.api.sprig.json", SampleFixtures.ApiRepo)]
    [InlineData("Sprig.Demo.web.sprig.json", SampleFixtures.WebRepo)]
    public void Repo_config_fixtures_parse_and_validate(string resource, string expectedName)
    {
        var config = SprigConfigLoader.Parse(SampleFixtures.Read(resource), resource);

        Assert.Equal(SprigConfigLoader.SupportedSchema, config.Schema);
        Assert.Equal(expectedName, config.Name);

        var result = SprigConfigValidator.Validate(config);
        Assert.True(result.IsValid,
            $"{resource} is not valid: {string.Join("; ", result.Issues.Select(i => i.ToString()))}");
    }

    [Fact]
    public void Map_fixture_composes_the_two_repos_with_no_gaps()
    {
        // Register the samples, then project the map: web's `api` need must resolve to sample-api's provide,
        // with no gaps or ambiguities — otherwise a checkout would report an unmet need.
        using var store = new TempStore();
        var registry = new RepoRegistryStore(store.Paths);
        var repos = new List<ResolvedRepo>();
        foreach (var (name, files) in new[]
                 {
                     (SampleFixtures.ApiRepo, SampleFixtures.ApiFiles),
                     (SampleFixtures.WebRepo, SampleFixtures.WebFiles),
                 })
        {
            var dir = Path.Combine(store.Root, "sample", name);
            SampleFixtures.WriteTo(files, dir);
            registry.Add(dir);
            repos.Add(new ResolvedRepo(name, dir, SprigConfigLoader.LoadFromFile(Path.Combine(dir, ".sprig.json"))));
        }

        var graph = MapGraphProjection.Project(SampleFixtures.Map(), repos);

        Assert.True(graph.IsComplete, "the sample map leaves a need unmet");
        Assert.Empty(graph.Gaps);
        Assert.Empty(graph.Ambiguities);
        var edge = Assert.Single(graph.Edges);
        Assert.Equal((SampleFixtures.WebRepo, "api", SampleFixtures.ApiRepo), (edge.FromRepo, edge.Need, edge.ToRepo));
    }

    [Fact]
    public void Map_fixture_saves_through_the_real_store()
    {
        using var store = new TempStore();
        var registry = new RepoRegistryStore(store.Paths);
        var maps = new MapStore(store.Paths, registry);

        // The store validates repo names against the registry, so register the samples first —
        // this is also what proves the fixture's repo names match the configs' `name` fields.
        foreach (var (name, files) in new[]
                 {
                     (SampleFixtures.ApiRepo, SampleFixtures.ApiFiles),
                     (SampleFixtures.WebRepo, SampleFixtures.WebFiles),
                 })
        {
            var dir = Path.Combine(store.Root, "sample", name);
            SampleFixtures.WriteTo(files, dir);
            registry.Add(dir);
        }

        maps.Save(SampleFixtures.Map());

        var saved = maps.Get(SampleFixtures.MapName);
        Assert.NotNull(saved);
        Assert.Equal([SampleFixtures.ApiRepo, SampleFixtures.WebRepo], saved!.Repos.Select(r => r.Name));
    }

    [Fact]
    public void Compose_fixture_declares_the_port_the_config_overrides()
    {
        var config = SprigConfigLoader.Parse(
            SampleFixtures.Read("Sprig.Demo.api.sprig.json"), "api.sprig.json");
        var yaml = SampleFixtures.Read("Sprig.Demo.api.docker-compose.yml");

        // The override targets services.db.ports[0]; if the compose fixture stops declaring that
        // path, ComposeGenerator throws at create time. Cheap structural check, clear failure.
        var compose = Assert.Single(Assert.Single(config.Modules).Compose);
        Assert.Contains(compose.Overrides, o => o.Path.SequenceEqual(new[] { "services", "db", "ports", "0" }));
        Assert.Contains("db:", yaml);
        Assert.Contains("ports:", yaml);
    }

}
