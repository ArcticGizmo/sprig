using Sprig.Core.Config;
using Sprig.Core.Stacks;
using Sprig.Core.Substitution;

namespace Sprig.Tests.Stacks;

public class StackScopeBuilderTests
{
    static SprigRepoConfig Cfg(string name, params (string k, string v)[] provides) => new()
    {
        Name = name,
        Provides = provides.ToDictionary(p => p.k, p => p.v),
    };

    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> Ports(
        params (string repo, string port, int value)[] items)
    {
        var map = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (repo, port, value) in items)
            (map.TryGetValue(repo, out var d) ? d : map[repo] = new())[port] = value;
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, int>)kv.Value);
    }

    [Fact]
    public void Same_named_ports_in_different_repos_are_isolated()
    {
        var repos = new[] { ("a", Cfg("a")), ("b", Cfg("b")) };
        var scope = StackScopeBuilder.Build("ws", repos, Ports(("a", "db", 20000), ("b", "db", 20001)));

        Assert.Equal("20000", SubstitutionEngine.Resolve("${sprig.ports.db}", scope.For("a")));
        Assert.Equal("20001", SubstitutionEngine.Resolve("${sprig.ports.db}", scope.For("b")));
    }

    [Fact]
    public void Repo_consumes_another_repos_provide()
    {
        var repos = new[]
        {
            ("dotnet-api", Cfg("dotnet-api", ("baseUrl", "http://localhost:${sprig.ports.http}"))),
            ("vue", Cfg("vue")),
        };
        var scope = StackScopeBuilder.Build("ws", repos, Ports(("dotnet-api", "http", 20001), ("vue", "frontend", 20002)));

        Assert.Equal("http://localhost:20001", scope.Provides["dotnet-api.baseUrl"]);
        Assert.Equal("http://localhost:20001",
            SubstitutionEngine.Resolve("${sprig.provides.dotnet-api.baseUrl}", scope.For("vue")));
    }

    [Fact]
    public void Stack_var_can_reference_a_provide()
    {
        var repos = new[] { ("api", Cfg("api", ("base", "http://localhost:${sprig.ports.http}"))), ("web", Cfg("web")) };
        var vars = new Dictionary<string, string> { ["apiBase"] = "${sprig.provides.api.base}" };
        var scope = StackScopeBuilder.Build("ws", repos, Ports(("api", "http", 20001), ("web", "p", 20002)), vars);

        Assert.Equal("http://localhost:20001", SubstitutionEngine.Resolve("${sprig.apiBase}", scope.For("web")));
    }

    [Fact]
    public void Consuming_a_missing_provide_throws_when_resolved()
    {
        var repos = new[] { ("api", Cfg("api", ("base", "x"))), ("web", Cfg("web")) };
        var scope = StackScopeBuilder.Build("ws", repos, Ports(("api", "http", 20001), ("web", "p", 20002)));

        Assert.Throws<SubstitutionException>(
            () => SubstitutionEngine.Resolve("${sprig.provides.api.missing}", scope.For("web")));
    }

    [Fact]
    public void Workspace_slug_available_to_every_repo()
    {
        var repos = new[] { ("a", Cfg("a")), ("b", Cfg("b")) };
        var scope = StackScopeBuilder.Build("feat-x", repos, Ports(("a", "p", 1), ("b", "p", 2)));
        Assert.Equal("db--feat-x", SubstitutionEngine.Resolve("db--${sprig.workspace}", scope.For("b")));
    }
}
