using Sprig.Core.Config;

namespace Sprig.Tests.Config;

public class ConfigReferencesTests
{
    [Fact]
    public void Undeclared_references_flag_refs_that_are_not_inputs_or_workspace()
    {
        var config = SprigConfigLoader.Parse("""
            {
              "schema": 1, "name": "vue",
              "inputs": [ { "name": "frontend", "example": "3000" } ],
              "env": [ { "file": ".env", "set": {
                  "PORT": "${sprig.frontend}",
                  "NAME": "app--${sprig.workspace}",
                  "OOPS": "${sprig.apiUrl}"
              } } ]
            }
            """);

        // frontend (declared) and workspace are fine; apiUrl is undeclared.
        Assert.Equal(["apiUrl"], ConfigReferences.UndeclaredReferences(config));
        Assert.Contains("frontend", ConfigReferences.ReferencedPaths(config));
        Assert.Contains("workspace", ConfigReferences.ReferencedPaths(config));
    }

    [Fact]
    public void Scans_compose_templates_too()
    {
        var config = SprigConfigLoader.Parse("""
            { "schema":1, "name":"api",
              "compose": { "file":"docker-compose.yml", "overrides":[
                  { "path":["services","x","image"], "template":"${sprig.imageTag}" } ] } }
            """);
        Assert.Equal(["imageTag"], ConfigReferences.UndeclaredReferences(config));
    }

    [Fact]
    public void Declared_inputs_are_not_flagged()
    {
        var config = SprigConfigLoader.Parse("""
            { "schema":1, "name":"api",
              "inputs":[ { "name":"imageTag" } ],
              "compose": { "file":"docker-compose.yml", "overrides":[
                  { "path":["services","x","image"], "template":"${sprig.imageTag}" } ] } }
            """);
        Assert.Empty(ConfigReferences.UndeclaredReferences(config));
    }

    [Fact]
    public void No_refs_yields_empty()
    {
        var config = SprigConfigLoader.Parse("""{ "schema":1, "name":"x" }""");
        Assert.Empty(ConfigReferences.UndeclaredReferences(config));
    }
}
