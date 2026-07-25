using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class StackSharesTests
{
    [Fact]
    public void Derive_groups_the_consumers_of_each_shared_port()
    {
        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string> { ["apiUrl"] = "http://localhost:${sprig.ports.api_port}" },
            ["api"] = new Dictionary<string, string>
            {
                ["port"] = "${sprig.ports.api_port}",
                ["dbPort"] = "${sprig.ports.postgres_port}",
            },
        };

        var shares = StackShares.Derive(["vue", "api"], ["api_port", "postgres_port"], bindings);

        var share = Assert.Single(shares);
        Assert.Equal("api_port", share.Port);
        Assert.Equal(
            new[] { ("api", "port"), ("vue", "apiUrl") },
            share.Consumers.Select(c => (c.Repo, c.Input)).OrderBy(t => t).ToArray());
    }

    [Fact]
    public void Derive_ignores_single_consumers_and_undeclared_ports()
    {
        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["a"] = new Dictionary<string, string> { ["x"] = "${sprig.ports.solo}" },        // single consumer
            ["b"] = new Dictionary<string, string> { ["y"] = "${sprig.ports.ghost}" },        // undeclared
            ["c"] = new Dictionary<string, string> { ["z"] = "${sprig.ports.ghost}" },        // undeclared
        };

        Assert.Empty(StackShares.Derive(["a", "b", "c"], ["solo"], bindings));
    }
}
