using Sprig.Core.Substitution;

namespace Sprig.Tests.Substitution;

public class SubstitutionEngineTests
{
    static string Resolve(string input, params (string key, string value)[] vars)
        => SubstitutionEngine.Resolve(input, new DictionaryVariableSource(
            vars.ToDictionary(v => v.key, v => v.value)));

    [Fact]
    public void Resolves_named_port()
        => Assert.Equal("http://localhost:20001",
            Resolve("http://localhost:${sprig.ports.api}", ("ports.api", "20001")));

    [Fact]
    public void Resolves_workspace_slug()
        => Assert.Equal("librarydb_postgres--feature-x",
            Resolve("librarydb_postgres--${sprig.workspace}", ("workspace", "feature-x")));

    [Fact]
    public void Resolves_multiple_refs_in_one_string()
        => Assert.Equal("20001:5432",
            Resolve("${sprig.ports.pg}:${sprig.container.port}",
                ("ports.pg", "20001"), ("container.port", "5432")));

    [Fact]
    public void Resolves_variable_to_variable_chain()
        => Assert.Equal("http://localhost:20001",
            Resolve("${sprig.apiUrl}",
                ("apiUrl", "http://localhost:${sprig.ports.api}"), ("ports.api", "20001")));

    [Fact]
    public void Resolves_cross_repo_provides()
        => Assert.Equal("http://localhost:20001",
            Resolve("${sprig.provides.dotnet-api.baseUrl}",
                ("provides.dotnet-api.baseUrl", "http://localhost:20001")));

    [Fact]
    public void Unknown_reference_throws()
    {
        var ex = Assert.Throws<SubstitutionException>(() => Resolve("${sprig.nope}"));
        Assert.Contains("unknown reference", ex.Message);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Direct_cycle_throws()
    {
        var ex = Assert.Throws<SubstitutionException>(() =>
            Resolve("${sprig.a}", ("a", "${sprig.b}"), ("b", "${sprig.a}")));
        Assert.Contains("cyclic", ex.Message);
    }

    [Fact]
    public void Self_cycle_throws()
        => Assert.Throws<SubstitutionException>(() => Resolve("${sprig.a}", ("a", "x${sprig.a}")));

    [Fact]
    public void Unterminated_reference_throws()
        => Assert.Throws<SubstitutionException>(() => Resolve("${sprig.ports.api"));

    [Fact]
    public void Empty_reference_throws()
        => Assert.Throws<SubstitutionException>(() => Resolve("${sprig.}"));

    [Fact]
    public void Non_sprig_dollar_expressions_pass_through()
    {
        const string s = "$HOME and ${OTHER} and ${sprig_not_a_ref} and $$";
        Assert.Equal(s, Resolve(s));
    }

    [Fact]
    public void Empty_input_is_empty()
        => Assert.Equal("", Resolve(""));

    [Fact]
    public void Whitespace_inside_reference_is_trimmed()
        => Assert.Equal("20001", Resolve("${sprig. ports.api }", ("ports.api", "20001")));
}
