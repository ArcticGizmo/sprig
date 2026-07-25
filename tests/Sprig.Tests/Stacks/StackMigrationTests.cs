using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class StackMigrationTests
{
    static StackDefinition SchemaOne(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings) => new()
    {
        Schema = 1,
        Name = "web+api",
        Repos = ["vue", "api"],
        Ports = ["frontend_port", "api_port", "postgres_port"],
        Bindings = bindings,
    };

    [Fact]
    public void Normalize_backfills_a_shared_port_from_the_bindings()
    {
        // Both vue.apiUrl and api.port reference api_port → that port is shared.
        var stack = SchemaOne(new Dictionary<string, IReadOnlyDictionary<string, string>>
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
        });

        var migrated = StackMigration.Normalize(stack);

        Assert.Equal(2, migrated.Schema);
        var share = Assert.Single(migrated.Shares);
        Assert.Equal("api_port", share.Port);
        Assert.Equal(
            new[] { ("api", "port"), ("vue", "apiUrl") },
            share.Consumers.Select(c => (c.Repo, c.Input)).OrderBy(t => t).ToArray());
    }

    [Fact]
    public void Normalize_leaves_unshared_ports_out_of_shares()
    {
        var stack = SchemaOne(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string> { ["frontend"] = "${sprig.ports.frontend_port}" },
            ["api"] = new Dictionary<string, string> { ["dbPort"] = "${sprig.ports.postgres_port}" },
        });

        var migrated = StackMigration.Normalize(stack);

        Assert.Equal(2, migrated.Schema);
        Assert.Empty(migrated.Shares);
    }

    [Fact]
    public void Normalize_ignores_references_to_undeclared_ports()
    {
        // Two bindings reference a port the stack doesn't declare — not a valid shared stack port.
        var stack = SchemaOne(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string> { ["apiUrl"] = "${sprig.ports.ghost}" },
            ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.ghost}" },
        });

        Assert.Empty(StackMigration.Normalize(stack).Shares);
    }

    [Fact]
    public void Normalize_is_a_noop_for_schema_two()
    {
        var explicitShare = new SharedPort { Port = "api_port", Consumers = [new PortConsumer { Repo = "api", Input = "port" }] };
        var stack = SchemaOne(new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" },
        }) with { Schema = 2, Shares = [explicitShare] };

        var migrated = StackMigration.Normalize(stack);

        Assert.Same(stack, migrated);
        Assert.Single(migrated.Shares);
    }
}
