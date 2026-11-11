using System.Text.Json;
using System.Text.Json.Nodes;
using Sprig.Engine.Definitions;
using Sprig.Model;

namespace Sprig.Tests.Definitions;

/// <summary>Each testbed definition read from its YAML matches the model its C# emits, so the port to YAML changed
/// nothing sprig plans. Goes with the C# files.</summary>
[Collection("dotnet-run")]
[Trait("Category", "Integration")]
public sealed class TestbedYamlTests : IDisposable
{
    readonly TempStore _store = new();

    public void Dispose() => _store.Dispose();

    public static TheoryData<string, string> Pairs => new()
    {
        { "repos/shop-services/sprig.cs", "repos/shop-services/sprig.yaml" },
        { "repos/shop-web/sprig.cs", "repos/shop-web/sprig.yaml" },
        { "repos/shop-mobile/sprig.cs", "repos/shop-mobile/sprig.yaml" },
        { "storefront.orchestration.cs", "storefront.orchestration.yaml" },
        { "catalog.orchestration.cs", "catalog.orchestration.yaml" },
    };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void The_yaml_reads_into_the_model_the_csharp_emits(string csharp, string yaml)
    {
        var testbed = Path.Combine(RepoRoot(), "testbed");
        var sdk = Path.Combine(RepoRoot(), "src", "Sprig.Sdk", "Sprig.Sdk.csproj");
        var copy = Path.Combine(_store.Root, csharp.Replace('/', '_'));
        Directory.CreateDirectory(_store.Root);
        var text = File.ReadAllText(Path.Combine(testbed, csharp)).Replace($"#:package {SdkReference.PackageId}@0.*", $"#:project {sdk}");
        File.WriteAllText(copy, text);
        var trust = new FileTrustStore(_store.Paths);
        trust.Approve(copy, DefinitionHash.Of(text));
        var emitted = new DefinitionRunner(trust, new DotnetRunHost(), _store.Paths,
            new DefinitionRunnerOptions { Timeout = TimeSpan.FromSeconds(120), AllowLocalSdkProject = true, UseCache = false }).Evaluate(copy);
        var expected = Assert.IsType<Evaluated>(emitted).Model;

        var yamlPath = Path.Combine(testbed, yaml);
        var read = DefinitionReader.Read(File.ReadAllText(yamlPath), yamlPath);
        Assert.True(read.Ok, string.Join("\n", read.Problems.Select(p => $"{p.Location?.Line}: {p.Message}")));

        Assert.Equal(Normalise(expected), Normalise(read.Model!));
    }

    /// <summary>The model without what's expected to differ: where things are written, the SDK's version, the order
    /// values are listed in, and <c>Section__Key</c> against <c>Section:Key</c> in a JSON file, which sprig writes the
    /// same.</summary>
    static string Normalise(DefinitionModel model)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(model, ModelJsonContext.Default.DefinitionModel))!;
        Strip(node);
        if (node["apps"]?.AsArray() is { } apps)
        {
            var byName = apps.Select(a => a!.DeepClone()).OrderBy(a => (string?)a["name"]).ToList();
            apps.Clear();
            foreach (var a in byName) apps.Add(a);
        }
        foreach (var app in node["apps"]?.AsArray() ?? [])
        {
            var env = app!["env"]!.AsArray();
            foreach (var e in env.Where(e => (string?)e!["kind"] == "appSettings"))
                e!["key"] = ((string)e["key"]!).Replace("__", ":");
            var sorted = env.Select(e => e!.DeepClone()).OrderBy(e => (string?)e["file"]).ThenBy(e => (string?)e["key"]).ToList();
            env.Clear();
            foreach (var e in sorted) env.Add(e);
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    static void Strip(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in (string[])["location", "sdkVersion", "schema"]) obj.Remove(key);
            foreach (var (_, child) in obj) Strip(child);
        }
        else if (node is JsonArray array)
            foreach (var child in array) Strip(child);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sprig.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
