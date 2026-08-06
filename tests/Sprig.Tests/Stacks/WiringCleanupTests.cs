using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class WiringCleanupTests
{
    static readonly IReadOnlyList<string> Repos = ["vue", "api"];

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Inputs =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["vue"] = ["frontend", "apiUrl"], // pin slots 0, 1
            ["api"] = ["port", "dbPort"],     // pin slots 2, 3
        };

    static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Bindings =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string>
            {
                ["frontend"] = "${sprig.ports.frontend_port}",              // → slot 0        → bary 0
                ["apiUrl"] = "http://localhost:${sprig.ports.api_port}",    // → slot 1
            },
            ["api"] = new Dictionary<string, string>
            {
                ["port"] = "${sprig.ports.api_port}",                       // → slot 2  (api_port bary = 1.5)
                ["dbPort"] = "${sprig.ports.postgres_port}",                // → slot 3        → bary 3
            },
        };

    static WiringGraph Build(IReadOnlyList<string> ports) =>
        WiringGraph.Build(Repos, ports, Inputs, Bindings);

    [Fact]
    public void Orders_ports_by_the_barycentre_of_the_inputs_they_feed()
    {
        IReadOnlyList<string> scrambled = ["postgres_port", "frontend_port", "api_port"];
        var ordered = WiringCleanup.OrderPorts(scrambled, Build(scrambled));
        Assert.Equal(["frontend_port", "api_port", "postgres_port"], ordered);
    }

    [Fact]
    public void An_already_tidy_rail_is_left_unchanged()
    {
        IReadOnlyList<string> tidy = ["frontend_port", "api_port", "postgres_port"];
        var ordered = WiringCleanup.OrderPorts(tidy, Build(tidy));
        Assert.Equal(tidy, ordered);
    }

    [Fact]
    public void Is_idempotent()
    {
        IReadOnlyList<string> scrambled = ["postgres_port", "frontend_port", "api_port"];
        var once = WiringCleanup.OrderPorts(scrambled, Build(scrambled));
        var twice = WiringCleanup.OrderPorts(once, Build(once));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Unconsumed_ports_keep_their_relative_order_and_sink_to_the_bottom()
    {
        IReadOnlyList<string> ports = ["ghost_b", "api_port", "ghost_a", "frontend_port"];
        var ordered = WiringCleanup.OrderPorts(ports, Build(ports));
        // Consumed ports rise (frontend_port bary 0, api_port bary 1.5); unconsumed keep input order.
        Assert.Equal(["frontend_port", "api_port", "ghost_b", "ghost_a"], ordered);
    }

    // A shared port forces repo order to matter: the two repos that consume it want to sit adjacent,
    // so the rail alone can't remove the crossing — the middle repo has to move out from between them.
    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SharedInputs =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["ra"] = ["a"],
            ["rb"] = ["b"],
            ["rc"] = ["c"],
        };

    static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SharedBindings =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["ra"] = new Dictionary<string, string> { ["a"] = "${sprig.ports.pshared}" },
            ["rb"] = new Dictionary<string, string> { ["b"] = "${sprig.ports.pb}" },
            ["rc"] = new Dictionary<string, string> { ["c"] = "${sprig.ports.pshared}" },
        };

    static WiringGraph BuildShared(IReadOnlyList<string> ports) =>
        WiringGraph.Build(["ra", "rb", "rc"], ports, SharedInputs, SharedBindings);

    [Fact]
    public void Tidy_reorders_repos_to_bring_shared_port_consumers_together()
    {
        // ra and rc both consume pshared; rb sits between them consuming pb. Tidy lifts rb out so the
        // two shared consumers become adjacent (ra, rc), which is the only way to drop the crossing.
        IReadOnlyList<string> ports = ["pshared", "pb"];
        IReadOnlyList<string> repos = ["ra", "rb", "rc"];
        var (_, orderedRepos) = WiringCleanup.Tidy(ports, repos, BuildShared(ports));
        Assert.Equal(["ra", "rc", "rb"], orderedRepos);
    }

    [Fact]
    public void Tidy_is_idempotent_across_both_columns()
    {
        IReadOnlyList<string> ports = ["postgres_port", "frontend_port", "api_port"];
        IReadOnlyList<string> repos = ["api", "vue"];
        var (p1, r1) = WiringCleanup.Tidy(ports, repos, Build(ports));
        var (p2, r2) = WiringCleanup.Tidy(p1, r1, Build(p1));
        Assert.Equal(p1, p2);
        Assert.Equal(r1, r2);
    }
}
