using Sprig.Core.Config;
using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class StackAutowireTests
{
    static InputDeclaration In(string name, string? example = null, string? allowed = null) =>
        new() { Name = name, Example = example, AllowedPorts = allowed };

    static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NoBindings =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    [Fact]
    public void Names_a_fresh_port_per_input_with_a_port_suffix()
    {
        var proposal = StackAutowire.Propose(
            [new AutowireRepo("vue", [In("frontend", "3000")])],
            [], NoBindings);

        Assert.Equal("${sprig.ports.frontend_port}", proposal.Bindings["vue"]["frontend"]);
        Assert.Contains("frontend_port", proposal.Ports);
    }

    [Fact]
    public void Keeps_an_existing_port_suffix_and_snake_cases_camel_names()
    {
        var proposal = StackAutowire.Propose(
            [new AutowireRepo("api", [In("dbPort", "5432"), In("port", "5000")])],
            [], NoBindings);

        Assert.Equal("${sprig.ports.db_port}", proposal.Bindings["api"]["dbPort"]);
        Assert.Equal("${sprig.ports.port}", proposal.Bindings["api"]["port"]);
    }

    [Fact]
    public void Wraps_a_url_input_as_a_localhost_transform_over_a_derived_port()
    {
        var proposal = StackAutowire.Propose(
            [new AutowireRepo("vue", [In("apiUrl", "http://localhost:4000")])],
            [], NoBindings);

        Assert.Equal("http://localhost:${sprig.ports.api_port}", proposal.Bindings["vue"]["apiUrl"]);
    }

    [Fact]
    public void Honours_an_https_example_scheme()
    {
        var proposal = StackAutowire.Propose(
            [new AutowireRepo("vue", [In("apiUrl", "https://localhost:4000")])],
            [], NoBindings);

        Assert.Equal("https://localhost:${sprig.ports.api_port}", proposal.Bindings["vue"]["apiUrl"]);
    }

    [Fact]
    public void Reuses_an_existing_port_whose_name_matches()
    {
        var proposal = StackAutowire.Propose(
            [new AutowireRepo("api", [In("dbPort", "5432")])],
            ["db_port"], NoBindings);

        Assert.Equal("${sprig.ports.db_port}", proposal.Bindings["api"]["dbPort"]);
        // no duplicate port introduced
        Assert.Equal(["db_port"], proposal.Ports);
    }

    [Fact]
    public void Never_overwrites_a_binding_the_user_already_typed()
    {
        var existing = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["vue"] = new Dictionary<string, string> { ["apiUrl"] = "http://localhost:4000" }, // literal the user chose
        };

        var proposal = StackAutowire.Propose(
            [new AutowireRepo("vue", [In("apiUrl", "http://localhost:4000"), In("frontend", "3000")])],
            [], existing);

        Assert.Equal("http://localhost:4000", proposal.Bindings["vue"]["apiUrl"]);       // preserved
        Assert.Equal("${sprig.ports.frontend_port}", proposal.Bindings["vue"]["frontend"]); // filled
    }

    [Fact]
    public void Gives_same_named_inputs_in_different_repos_distinct_ports()
    {
        // Two services that each own a dbPort must NOT be pointed at one port by default.
        var proposal = StackAutowire.Propose(
            [
                new AutowireRepo("api", [In("dbPort", "5432")]),
                new AutowireRepo("worker", [In("dbPort", "5432")]),
            ],
            [], NoBindings);

        var apiPort = proposal.Bindings["api"]["dbPort"];
        var workerPort = proposal.Bindings["worker"]["dbPort"];
        Assert.NotEqual(apiPort, workerPort);
        Assert.Equal("${sprig.ports.db_port}", apiPort);
        Assert.Equal("${sprig.ports.db_port_2}", workerPort);
    }

    [Fact]
    public void Keeps_ports_referenced_by_a_preserved_binding_in_the_port_list()
    {
        var existing = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" },
        };

        var proposal = StackAutowire.Propose(
            [new AutowireRepo("api", [In("port", "5000")])],
            [], existing);

        Assert.Contains("api_port", proposal.Ports);
    }

    [Fact]
    public void A_second_run_is_idempotent()
    {
        var repos = new[] { new AutowireRepo("vue", new[] { In("frontend", "3000"), In("apiUrl", "http://x:4000") }) };
        var first = StackAutowire.Propose(repos, [], NoBindings);
        var second = StackAutowire.Propose(repos, first.Ports, first.Bindings);

        Assert.Equal(first.Ports, second.Ports);
        Assert.Equal(first.Bindings["vue"]["frontend"], second.Bindings["vue"]["frontend"]);
        Assert.Equal(first.Bindings["vue"]["apiUrl"], second.Bindings["vue"]["apiUrl"]);
    }
}
