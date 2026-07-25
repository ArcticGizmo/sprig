using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class BindingClassifierTests
{
    static ISet<string> Set(params string[] items) => new HashSet<string>(items);

    [Theory]
    [InlineData("", BindingKind.Unbound)]
    [InlineData("   ", BindingKind.Unbound)]
    [InlineData("${sprig.ports.api_port}", BindingKind.Identity)]
    [InlineData("http://localhost:${sprig.ports.api_port}", BindingKind.Transform)]
    [InlineData("http://localhost:4000", BindingKind.Literal)]
    [InlineData("Host=localhost;Port=${sprig.ports.db};Db=x", BindingKind.Transform)]
    public void Classifies_by_shape(string expr, BindingKind expected)
    {
        var c = BindingClassifier.Classify(expr, Set("api_port", "db"), Set());
        Assert.Equal(expected, c.Kind);
    }

    [Fact]
    public void An_identity_mapping_to_its_own_port_is_collapsible()
    {
        var c = BindingClassifier.Classify("${sprig.ports.api_port}", Set("api_port"), Set());
        Assert.True(c.IsCollapsible);
        Assert.False(c.IsException);
    }

    [Fact]
    public void A_shared_identity_is_an_exception_not_collapsible()
    {
        var c = BindingClassifier.Classify("${sprig.ports.api_port}", Set("api_port"), Set("api_port"));
        Assert.True(c.Shared);
        Assert.False(c.IsCollapsible);
        Assert.True(c.IsException);
    }

    [Fact]
    public void A_reference_to_an_undeclared_port_never_collapses()
    {
        var c = BindingClassifier.Classify("${sprig.ports.ghost}", Set("api_port"), Set());
        Assert.True(c.ReferencesUndeclaredPort);
        Assert.False(c.IsCollapsible);
    }

    [Fact]
    public void ClassifyAll_finds_the_shared_port_across_repos()
    {
        var bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
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
        };

        var all = BindingClassifier.ClassifyAll(bindings, ["frontend_port", "api_port", "postgres_port"]);

        // api_port is shared by vue.apiUrl (transform) and api.port (identity)
        Assert.True(all[("api", "port")].Shared);
        Assert.True(all[("vue", "apiUrl")].Shared);
        // the two single-consumer ports are plain identities that collapse
        Assert.True(all[("vue", "frontend")].IsCollapsible);
        Assert.True(all[("api", "dbPort")].IsCollapsible);
        // the shared identity does not collapse
        Assert.False(all[("api", "port")].IsCollapsible);
    }
}
