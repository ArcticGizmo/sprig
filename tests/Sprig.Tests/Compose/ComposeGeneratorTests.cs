using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Substitution;
using YamlDotNet.RepresentationModel;

namespace Sprig.Tests.Compose;

public class ComposeGeneratorTests
{
    // Mirrors sprig-example-dotnet's compose.
    const string Source = """
        services:
          postgres:
            image: postgres:17
            container_name: librarydb_postgres
            environment:
              POSTGRES_USER: library
              POSTGRES_DB: librarydb
            ports:
              - "6050:5432"
            restart: unless-stopped
        """;

    static ComposeConfig Config() => new()
    {
        File = "docker-compose.yml",
        Overrides =
        [
            new ComposeOverride { Path = ["services", "postgres", "container_name"], Template = "librarydb_postgres--${sprig.workspace}" },
            new ComposeOverride { Path = ["services", "postgres", "ports", "0"], Template = "${sprig.ports.postgres}:5432" },
        ],
    };

    static IVariableSource Scope() =>
        SprigScope.ForWorkspace("feat-x", new Dictionary<string, int> { ["postgres"] = 20002 });

    static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    static YamlNode At(YamlMappingNode root, params string[] path)
    {
        YamlNode node = root;
        foreach (var seg in path)
            node = node is YamlSequenceNode seq ? seq.Children[int.Parse(seg)] : ((YamlMappingNode)node).Children[new YamlScalarNode(seg)];
        return node;
    }

    [Fact]
    public void Overrides_container_name_and_port_and_resolves_templates()
    {
        var yaml = new ComposeGenerator().Generate(Source, Config(), Scope());
        var root = Parse(yaml);

        Assert.Equal("librarydb_postgres--feat-x", ((YamlScalarNode)At(root, "services", "postgres", "container_name")).Value);
        Assert.Equal("20002:5432", ((YamlScalarNode)At(root, "services", "postgres", "ports", "0")).Value);
    }

    [Fact]
    public void Preserves_untouched_keys()
    {
        var yaml = new ComposeGenerator().Generate(Source, Config(), Scope());
        var root = Parse(yaml);

        Assert.Equal("postgres:17", ((YamlScalarNode)At(root, "services", "postgres", "image")).Value);
        Assert.Equal("library", ((YamlScalarNode)At(root, "services", "postgres", "environment", "POSTGRES_USER")).Value);
        Assert.Equal("unless-stopped", ((YamlScalarNode)At(root, "services", "postgres", "restart")).Value);
    }

    [Fact]
    public void Missing_path_throws()
    {
        var cfg = new ComposeConfig
        {
            File = "docker-compose.yml",
            Overrides = [new ComposeOverride { Path = ["services", "nope", "container_name"], Template = "x" }],
        };
        var ex = Assert.Throws<ComposeException>(() => new ComposeGenerator().Generate(Source, cfg, Scope()));
        Assert.Contains("path not found", ex.Message);
    }

    [Fact]
    public void Out_of_range_index_throws()
    {
        var cfg = new ComposeConfig
        {
            File = "docker-compose.yml",
            Overrides = [new ComposeOverride { Path = ["services", "postgres", "ports", "5"], Template = "x" }],
        };
        Assert.Throws<ComposeException>(() => new ComposeGenerator().Generate(Source, cfg, Scope()));
    }

    [Fact]
    public void GenerateToFile_writes_central_file()
    {
        using var store = new TempStore();
        var src = Path.Combine(store.Root, "docker-compose.yml");
        Directory.CreateDirectory(store.Root);
        File.WriteAllText(src, Source);
        var dest = Path.Combine(store.Root, "instances", "feat-x", "docker-compose.sprig.yml");

        var written = new ComposeGenerator().GenerateToFile(src, Config(), Scope(), dest);

        Assert.Equal(dest, written);
        Assert.True(File.Exists(dest));
        Assert.Contains("librarydb_postgres--feat-x", File.ReadAllText(dest));
    }
}
