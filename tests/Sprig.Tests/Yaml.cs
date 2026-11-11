using Sprig.Engine.Definitions;
using Sprig.Model;

namespace Sprig.Tests;

/// <summary>Models for tests, read from YAML the way sprig reads a definition, so a fixture is written in the format
/// itself and a test fails on its file's line when the format and the test disagree.</summary>
static class Yaml
{
    public static RepoModel Repo(string yaml, string file = "sprig.yaml") => Read<RepoModel>(yaml, file);

    public static OrchestrationModel Orchestration(string yaml, string file = "storefront.orchestration.yaml") => Read<OrchestrationModel>(yaml, file);

    static T Read<T>(string yaml, string file) where T : DefinitionModel
    {
        var result = DefinitionReader.Read(yaml, file);
        Assert.True(result.Ok, string.Join("\n", result.Problems.Select(p => $"{file}:{p.Location?.Line}: {p.Message}")));
        return Assert.IsType<T>(result.Model);
    }
}
