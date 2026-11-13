using System.Text.Json.Nodes;
using Sprig.Engine.Definitions;

namespace Sprig.Tests.Definitions;

/// <summary>The schema IDEs read is published from docs/schema, so it has to be what sprig generates from the keys it
/// reads. Run with SPRIG_WRITE_SCHEMA=1 to write the files after changing the format.</summary>
public class DefinitionSchemaTests
{
    public static TheoryData<string> Kinds => ["repo", "orchestration"];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_published_schema_is_the_one_sprig_generates(string kind)
    {
        var generated = kind == "repo" ? DefinitionSchema.Repo() : DefinitionSchema.Orchestration();
        var file = Path.Combine(RepoRoot(), "docs", "schema", $"v{DefinitionReader.CurrentSchema}", $"{kind}.json");
        if (Environment.GetEnvironmentVariable("SPRIG_WRITE_SCHEMA") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, generated);
        }

        Assert.True(File.Exists(file), $"{file} is missing; run this test with SPRIG_WRITE_SCHEMA=1");
        Assert.Equal(generated, File.ReadAllText(file).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Every_key_the_reader_takes_is_in_the_schema_with_a_description()
    {
        var repo = JsonNode.Parse(DefinitionSchema.Repo())!;
        var app = repo["properties"]!["apps"]!["additionalProperties"]!["properties"]!.AsObject();

        Assert.Equal(DefinitionKeys.Names(DefinitionKeys.Repo), repo["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(DefinitionKeys.Names(DefinitionKeys.App), app.Select(p => p.Key));
        Assert.All(app, p => Assert.False(string.IsNullOrEmpty((string?)p.Value!["description"])));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sprig.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
