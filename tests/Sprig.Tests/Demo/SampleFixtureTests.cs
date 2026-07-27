using Sprig.Core.Config;
using Sprig.Core.Demo;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

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
    public void Stack_fixture_binds_every_input_both_repos_declare()
    {
        var stack = SampleFixtures.Stack();

        foreach (var (resource, repo) in new[]
                 {
                     ("Sprig.Demo.api.sprig.json", SampleFixtures.ApiRepo),
                     ("Sprig.Demo.web.sprig.json", SampleFixtures.WebRepo),
                 })
        {
            var config = SprigConfigLoader.Parse(SampleFixtures.Read(resource), resource);
            var bindings = stack.Bindings[repo];
            foreach (var input in config.Inputs)
                Assert.True(bindings.ContainsKey(input.Name),
                    $"stack fixture does not bind {repo}.{input.Name} — create would fail at runtime");
        }
    }

    [Fact]
    public void Stack_fixture_saves_through_the_real_store()
    {
        using var store = new TempStore();
        var registry = new RepoRegistryStore(store.Paths);
        var stacks = new StackStore(store.Paths, registry, new InstanceStore(store.Paths));

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

        stacks.Save(SampleFixtures.Stack());

        var saved = stacks.Get(SampleFixtures.StackName);
        Assert.NotNull(saved);
        Assert.Equal([SampleFixtures.ApiRepo, SampleFixtures.WebRepo], saved!.Repos);
        // The shared port is the tour's centrepiece: one port, two consumers.
        var share = Assert.Single(saved.Shares);
        Assert.Equal(SampleFixtures.ApiPort, share.Port);
        Assert.Equal(2, share.Consumers.Count);
    }

    [Fact]
    public void Compose_fixture_declares_the_port_the_config_overrides()
    {
        var config = SprigConfigLoader.Parse(
            SampleFixtures.Read("Sprig.Demo.api.sprig.json"), "api.sprig.json");
        var yaml = SampleFixtures.Read("Sprig.Demo.api.docker-compose.yml");

        // The override targets services.db.ports[0]; if the compose fixture stops declaring that
        // path, ComposeGenerator throws at create time. Cheap structural check, clear failure.
        var compose = Assert.Single(config.Compose);
        Assert.Contains(compose.Overrides, o => o.Path.SequenceEqual(new[] { "services", "db", "ports", "0" }));
        Assert.Contains("db:", yaml);
        Assert.Contains("ports:", yaml);
    }

    [Fact]
    public void Port_names_the_stack_declares_are_the_ones_it_binds()
    {
        var stack = SampleFixtures.Stack();
        var declared = stack.Ports.ToHashSet();

        foreach (var (repo, bindings) in stack.Bindings)
            foreach (var (input, expression) in bindings)
                foreach (var port in PortExpressions.ReferencedPorts(expression))
                    Assert.True(declared.Contains(port),
                        $"{repo}.{input} references undeclared port '{port}'");
    }
}
